using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Sources.WarApi;

namespace FoxData.Worker;

public enum WarApiStaticMapNormalizationStatus
{
    Normalized,
    Rejected,
}

public sealed record WarApiStaticMapNormalizationResult(
    WarApiStaticMapNormalizationStatus Status,
    NormalizationRunDescriptor NormalizationRun,
    MapSnapshotResult? Snapshot);

public sealed class WarApiStaticMapNormalizationCoordinator(
    ICanonicalEvidenceReader evidenceReader,
    MapSnapshotKernel mapSnapshots,
    NormalizationKernel normalization,
    WarApiWorkerOptions options,
    TimeProvider timeProvider)
{
    private const string SemanticPrefix = "map-static/";
    private readonly WarApiParser _parser = new();

    public async Task<WarApiStaticMapNormalizationResult> NormalizeAsync(
        SourceParseRunId sourceParseRunId,
        CancellationToken cancellationToken)
    {
        var existingRun = await normalization.GetAsync(
            sourceParseRunId,
            WarApiVersions.StaticMapNormalizer,
            cancellationToken);

        if (existingRun is not null)
        {
            return await ReplayTerminalAsync(
                existingRun,
                cancellationToken);
        }

        var startedAt = timeProvider.GetUtcNow();
        var evidence = await evidenceReader.GetAsync(
            sourceParseRunId,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"Source parse run {sourceParseRunId} does not exist or has no replayable representation.");

        var sourceMapName = ValidateReplayContext(evidence);

        if (evidence.ParseOutcome is not ("parsed" or "parsed_with_unknowns"))
        {
            return await RejectAsync(
                sourceParseRunId,
                startedAt,
                "source_parse_unsuccessful",
                cancellationToken);
        }

        var decoded = await DecodeDurableRepresentationAsync(
            evidence,
            cancellationToken);
        var parsed = _parser.Parse(
            WarApiCapabilities.StaticMapState,
            decoded);

        VerifyReplay(
            evidence,
            parsed,
            decoded.LongLength);

        if (parsed.Value is not WarApiMapDataDto source)
        {
            throw new CanonicalStateIntegrityException(
                "Successful static-map replay did not produce a map-data DTO.");
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
                WarApiVersions.StaticMapNormalizer,
                WarApiCapabilities.StaticMapState.Key,
                evidence.SemanticKey,
                startedAt,
                completedAt,
                evidence.RepresentationFetchId,
                MapSnapshotKind.Static,
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
                "map-static"),
            new KeyValuePair<string, object?>(
                "outcome",
                "normalized"));

        return new WarApiStaticMapNormalizationResult(
            WarApiStaticMapNormalizationStatus.Normalized,
            result.NormalizationRun,
            result);
    }

    private async Task<WarApiStaticMapNormalizationResult> ReplayTerminalAsync(
        NormalizationRunDescriptor existingRun,
        CancellationToken cancellationToken)
    {
        if (existingRun.Outcome == NormalizationRunOutcome.Rejected)
        {
            return new WarApiStaticMapNormalizationResult(
                WarApiStaticMapNormalizationStatus.Rejected,
                existingRun,
                null);
        }

        if (existingRun.Outcome != NormalizationRunOutcome.Normalized ||
            existingRun.ErrorCode is not null)
        {
            throw new CanonicalStateIntegrityException(
                $"Static-map normalization run {existingRun.Id} has terminal outcome {existingRun.Outcome} and cannot be replayed as accepted work.");
        }

        var snapshot = await mapSnapshots.GetByNormalizationRunAsync(
            existingRun.Id,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"Normalized static-map run {existingRun.Id} has no durable map snapshot.");

        if (snapshot.Snapshot.Kind != MapSnapshotKind.Static)
        {
            throw new CanonicalStateIntegrityException(
                $"Static-map normalization run {existingRun.Id} resolves to a non-static snapshot.");
        }

        return new WarApiStaticMapNormalizationResult(
            WarApiStaticMapNormalizationStatus.Normalized,
            existingRun,
            snapshot);
    }

    private async Task<WarApiStaticMapNormalizationResult> RejectAsync(
        SourceParseRunId sourceParseRunId,
        DateTimeOffset startedAt,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var run = await normalization.RecordAsync(
            new NormalizationRunWrite(
                sourceParseRunId,
                WarApiVersions.StaticMapNormalizer,
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
                "map-static"),
            new KeyValuePair<string, object?>(
                "outcome",
                "rejected"),
            new KeyValuePair<string, object?>(
                "reason",
                errorCode));

        return new WarApiStaticMapNormalizationResult(
            WarApiStaticMapNormalizationStatus.Rejected,
            run,
            null);
    }

    private async Task<byte[]> DecodeDurableRepresentationAsync(
        CanonicalEvidenceInput evidence,
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
                $"Durable static-map representation for parse run {evidence.SourceParseRunId} can no longer be decoded: {exception.Message}");
        }
    }

    private static string ValidateReplayContext(
        CanonicalEvidenceInput evidence)
    {
        if (!string.Equals(
                evidence.SourceKey,
                WarApiCatalog.SourceKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.ParseCapabilityKey,
                WarApiCapabilities.StaticMapState.Key,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.EndpointCapabilityKey,
                WarApiCapabilities.StaticMapState.Key,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.ParseCapabilityKey,
                evidence.EndpointCapabilityKey,
                StringComparison.Ordinal) ||
            !evidence.SemanticKey.StartsWith(
                SemanticPrefix,
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Static-map normalization was asked to consume evidence from a different source capability.");
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
                $"Static-map normalizer {WarApiVersions.StaticMapNormalizer} cannot replay adapter/parser identity " +
                $"'{evidence.AdapterVersion}/{evidence.ParserVersion}/{evidence.FingerprintAlgorithm}'.");
        }

        var sourceMapName = evidence.SemanticKey[SemanticPrefix.Length..];

        try
        {
            return WarApiCatalog.ValidateMapName(sourceMapName);
        }
        catch (ArgumentException exception)
        {
            throw new CanonicalStateIntegrityException(
                $"Static-map evidence has an invalid durable source map identity: {exception.Message}");
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
