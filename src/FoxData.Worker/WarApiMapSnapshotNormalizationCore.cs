using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Sources.Abstractions;
using FoxData.Sources.WarApi;

namespace FoxData.Worker;

internal enum WarApiMapSnapshotNormalizationStatus
{
    Normalized,
    Rejected,
}

internal sealed record WarApiMapSnapshotNormalizationProfile(
    SourceCapability Capability,
    string SemanticPrefix,
    string NormalizerVersion,
    MapSnapshotKind SnapshotKind,
    string TelemetryDomain,
    string DisplayName);

internal sealed record WarApiMapSnapshotNormalizationResult(
    WarApiMapSnapshotNormalizationStatus Status,
    NormalizationRunDescriptor NormalizationRun,
    MapSnapshotResult? Snapshot);

internal sealed class WarApiMapSnapshotNormalizationCore(
    ICanonicalEvidenceReader evidenceReader,
    MapSnapshotKernel mapSnapshots,
    NormalizationKernel normalization,
    WarApiWorkerOptions options,
    TimeProvider timeProvider)
{
    private readonly WarApiParser _parser = new();

    public async Task<WarApiMapSnapshotNormalizationResult> NormalizeAsync(
        SourceParseRunId sourceParseRunId,
        WarApiMapSnapshotNormalizationProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var existingRun = await normalization.GetAsync(
            sourceParseRunId,
            profile.NormalizerVersion,
            cancellationToken);

        if (existingRun is not null)
        {
            return await ReplayTerminalAsync(
                existingRun,
                profile,
                cancellationToken);
        }

        var startedAt = timeProvider.GetUtcNow();
        var evidence = await evidenceReader.GetAsync(
            sourceParseRunId,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"Source parse run {sourceParseRunId} does not exist or has no replayable representation.");

        var sourceMapName = ValidateReplayContext(
            evidence,
            profile);

        if (evidence.ParseOutcome is not ("parsed" or "parsed_with_unknowns"))
        {
            return await RejectAsync(
                sourceParseRunId,
                startedAt,
                profile,
                "source_parse_unsuccessful",
                cancellationToken);
        }

        var decoded = await DecodeDurableRepresentationAsync(
            evidence,
            profile,
            cancellationToken);
        var parsed = _parser.Parse(
            profile.Capability,
            decoded);

        VerifyReplay(
            evidence,
            parsed,
            decoded.LongLength);

        if (parsed.Value is not WarApiMapDataDto source)
        {
            throw new CanonicalStateIntegrityException(
                $"Successful {profile.DisplayName} replay did not produce a map-data DTO.");
        }

        var items = source.MapItems is null
            ? Array.Empty<MapItemOccurrenceCandidate>()
            : source.MapItems
                .Select(
                    (item, ordinal) =>
                        new MapItemOccurrenceCandidate(
                            ordinal,
                            item.TeamId,
                            item.IconType,
                            item.X,
                            item.Y,
                            item.Flags,
                            item.ViewDirection))
                .ToArray();

        var textItems = source.MapTextItems is null
            ? Array.Empty<MapTextOccurrenceCandidate>()
            : source.MapTextItems
                .Select(
                    (item, ordinal) =>
                        new MapTextOccurrenceCandidate(
                            ordinal,
                            item.Text,
                            item.X,
                            item.Y,
                            item.MapMarkerType))
                .ToArray();

        var completedAt = timeProvider.GetUtcNow();
        var result = await mapSnapshots.RecordAcceptedAsync(
            new MapSnapshotWrite(
                sourceParseRunId,
                profile.NormalizerVersion,
                profile.Capability.Key,
                evidence.SemanticKey,
                startedAt,
                completedAt,
                evidence.RepresentationFetchId,
                profile.SnapshotKind,
                sourceMapName,
                source.RegionId,
                source.ScorchedVictoryTowns,
                source.Version,
                source.LastUpdated,
                TryUnixMilliseconds(source.LastUpdated),
                source.MapItems is not null,
                source.MapTextItems is not null,
                items,
                textItems),
            cancellationToken);

        WarApiTelemetry.NormalizationRuns.Add(
            1,
            new KeyValuePair<string, object?>(
                "source",
                WarApiCatalog.SourceKey),
            new KeyValuePair<string, object?>(
                "environment",
                evidence.Environment),
            new KeyValuePair<string, object?>(
                "shard",
                evidence.ShardKey),
            new KeyValuePair<string, object?>(
                "domain",
                profile.TelemetryDomain),
            new KeyValuePair<string, object?>(
                "outcome",
                "normalized"));

        return new WarApiMapSnapshotNormalizationResult(
            WarApiMapSnapshotNormalizationStatus.Normalized,
            result.NormalizationRun,
            result);
    }

    private async Task<WarApiMapSnapshotNormalizationResult>
        ReplayTerminalAsync(
            NormalizationRunDescriptor existingRun,
            WarApiMapSnapshotNormalizationProfile profile,
            CancellationToken cancellationToken)
    {
        if (existingRun.Outcome == NormalizationRunOutcome.Rejected)
        {
            return new WarApiMapSnapshotNormalizationResult(
                WarApiMapSnapshotNormalizationStatus.Rejected,
                existingRun,
                null);
        }

        if (existingRun.Outcome != NormalizationRunOutcome.Normalized ||
            existingRun.ErrorCode is not null)
        {
            throw new CanonicalStateIntegrityException(
                $"{profile.DisplayName} normalization run {existingRun.Id} has terminal outcome {existingRun.Outcome} and cannot be replayed as accepted work.");
        }

        var snapshot = await mapSnapshots.GetByNormalizationRunAsync(
            existingRun.Id,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"Normalized {profile.DisplayName} run {existingRun.Id} has no durable map snapshot.");

        if (snapshot.Snapshot.Kind != profile.SnapshotKind)
        {
            throw new CanonicalStateIntegrityException(
                $"{profile.DisplayName} normalization run {existingRun.Id} resolves to snapshot kind {snapshot.Snapshot.Kind}.");
        }

        return new WarApiMapSnapshotNormalizationResult(
            WarApiMapSnapshotNormalizationStatus.Normalized,
            existingRun,
            snapshot);
    }

    private async Task<WarApiMapSnapshotNormalizationResult> RejectAsync(
        SourceParseRunId sourceParseRunId,
        DateTimeOffset startedAt,
        WarApiMapSnapshotNormalizationProfile profile,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var run = await normalization.RecordAsync(
            new NormalizationRunWrite(
                sourceParseRunId,
                profile.NormalizerVersion,
                NormalizationRunOutcome.Rejected,
                errorCode,
                startedAt,
                timeProvider.GetUtcNow()),
            cancellationToken);

        WarApiTelemetry.NormalizationRuns.Add(
            1,
            new KeyValuePair<string, object?>(
                "source",
                WarApiCatalog.SourceKey),
            new KeyValuePair<string, object?>(
                "domain",
                profile.TelemetryDomain),
            new KeyValuePair<string, object?>(
                "outcome",
                "rejected"),
            new KeyValuePair<string, object?>(
                "reason",
                errorCode));

        return new WarApiMapSnapshotNormalizationResult(
            WarApiMapSnapshotNormalizationStatus.Rejected,
            run,
            null);
    }

    private async Task<byte[]> DecodeDurableRepresentationAsync(
        CanonicalEvidenceInput evidence,
        WarApiMapSnapshotNormalizationProfile profile,
        CancellationToken cancellationToken)
    {
        var maximumDecodedBytes = options.MaxDecodedBytes;
        if (evidence.DecodedByteLength is { } durableDecodedLength)
        {
            if (durableDecodedLength is < 0 or > int.MaxValue)
            {
                throw new CanonicalStateIntegrityException(
                    $"Source parse run {evidence.SourceParseRunId} has an invalid decoded byte length.");
            }

            maximumDecodedBytes = Math.Max(
                maximumDecodedBytes,
                (int)durableDecodedLength);
        }

        var maximumExpansionRatio = options.MaxExpansionRatio;
        if (evidence.DecodedByteLength is { } decodedLength &&
            evidence.Body.Length > 0)
        {
            maximumExpansionRatio = Math.Max(
                maximumExpansionRatio,
                Math.Max(
                    1d,
                    decodedLength / (double)evidence.Body.Length));
        }

        try
        {
            return await WarApiContentPolicy.DecodeAsync(
                evidence.Body,
                evidence.ContentEncoding,
                maximumDecodedBytes,
                maximumExpansionRatio,
                cancellationToken);
        }
        catch (WarApiDecodingException exception)
        {
            throw new CanonicalStateIntegrityException(
                $"Durable {profile.DisplayName} representation for parse run {evidence.SourceParseRunId} can no longer be decoded: {exception.Message}");
        }
    }

    private static string ValidateReplayContext(
        CanonicalEvidenceInput evidence,
        WarApiMapSnapshotNormalizationProfile profile)
    {
        if (!string.Equals(
                evidence.SourceKey,
                WarApiCatalog.SourceKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.ParseCapabilityKey,
                profile.Capability.Key,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.EndpointCapabilityKey,
                profile.Capability.Key,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.ParseCapabilityKey,
                evidence.EndpointCapabilityKey,
                StringComparison.Ordinal) ||
            !evidence.SemanticKey.StartsWith(
                profile.SemanticPrefix,
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                $"{profile.DisplayName} normalization was asked to consume evidence from a different source capability.");
        }

        if (!string.Equals(
                evidence.AdapterVersion,
                WarApiVersions.Adapter,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.ParserVersion,
                WarApiVersions.Parser,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.FingerprintAlgorithm,
                JsonStructuralFingerprinter.Algorithm,
                StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"{profile.DisplayName} normalizer {profile.NormalizerVersion} cannot replay adapter/parser identity " +
                $"'{evidence.AdapterVersion}/{evidence.ParserVersion}/{evidence.FingerprintAlgorithm}'.");
        }

        var sourceMapName =
            evidence.SemanticKey[profile.SemanticPrefix.Length..];

        try
        {
            return WarApiCatalog.ValidateMapName(sourceMapName);
        }
        catch (ArgumentException exception)
        {
            throw new CanonicalStateIntegrityException(
                $"{profile.DisplayName} evidence has an invalid durable source map identity: {exception.Message}");
        }
    }

    private static void VerifyReplay(
        CanonicalEvidenceInput evidence,
        WarApiParseResult replay,
        long decodedByteLength)
    {
        var replayOutcome = replay.Outcome switch
        {
            WarApiParseOutcome.Parsed => "parsed",
            WarApiParseOutcome.ParsedWithUnknowns =>
                "parsed_with_unknowns",
            WarApiParseOutcome.MalformedJson =>
                "malformed_json",
            WarApiParseOutcome.IncompatibleShape =>
                "incompatible_shape",
            _ => "unknown",
        };

        if (!replay.Parsed ||
            !string.Equals(
                replayOutcome,
                evidence.ParseOutcome,
                StringComparison.Ordinal) ||
            !string.Equals(
                replay.StructuralFingerprint,
                evidence.StructuralFingerprint,
                StringComparison.Ordinal) ||
            replay.UnknownPropertyCount !=
                evidence.UnknownPropertyCount ||
            replay.UnknownCodeCount !=
                evidence.UnknownCodeCount ||
            (evidence.DecodedByteLength is { } expectedLength &&
                expectedLength != decodedByteLength))
        {
            throw new CanonicalStateIntegrityException(
                $"Deterministic parser replay for source parse run {evidence.SourceParseRunId} did not match its durable parse metadata.");
        }
    }

    private static DateTimeOffset? TryUnixMilliseconds(long? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
