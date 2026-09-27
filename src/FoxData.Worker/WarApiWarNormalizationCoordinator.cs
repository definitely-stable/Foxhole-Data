using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Sources.WarApi;

namespace FoxData.Worker;

public enum WarApiWarNormalizationStatus
{
    Normalized,
    Rejected,
}

public sealed record WarApiWarNormalizationResult(
    WarApiWarNormalizationStatus Status,
    NormalizationRunDescriptor NormalizationRun,
    WarCanonicalResult? Canonical);

public sealed class WarApiWarNormalizationCoordinator(
    ICanonicalEvidenceReader evidenceReader,
    WarCanonicalKernel warCanonical,
    NormalizationKernel normalization,
    WarApiWorkerOptions options,
    TimeProvider timeProvider)
{
    private readonly WarApiParser _parser = new();

    public async Task<WarApiWarNormalizationResult> NormalizeAsync(
        SourceParseRunId sourceParseRunId,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();

        var evidence = await evidenceReader.GetAsync(
            sourceParseRunId,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"Source parse run {sourceParseRunId} does not exist or has no replayable representation.");

        ValidateReplayContext(evidence);

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
            WarApiCapabilities.RuntimeWarState,
            decoded);

        VerifyReplay(evidence, parsed, decoded.LongLength);

        if (parsed.Value is not WarApiWarStateDto source)
        {
            throw new CanonicalStateIntegrityException(
                "Successful War API replay did not produce a war-state DTO.");
        }

        if (!TryMapSnapshot(
                source,
                out var snapshot,
                out var rejectionCode))
        {
            return await RejectAsync(
                sourceParseRunId,
                startedAt,
                rejectionCode!,
                cancellationToken);
        }

        var completedAt = timeProvider.GetUtcNow();
        var canonical = await warCanonical.RecordAcceptedAsync(
            sourceParseRunId,
            evidence.ShardId,
            evidence.RepresentationFetchId,
            evidence.RetrievedAt,
            snapshot!,
            WarApiVersions.WarNormalizer,
            startedAt,
            completedAt,
            cancellationToken);

        WarApiTelemetry.NormalizationRuns.Add(
            1,
            new KeyValuePair<string, object?>("source", WarApiCatalog.SourceKey),
            new KeyValuePair<string, object?>("environment", evidence.Environment),
            new KeyValuePair<string, object?>("shard", evidence.ShardKey),
            new KeyValuePair<string, object?>("domain", "war"),
            new KeyValuePair<string, object?>("outcome", "normalized"));

        return new WarApiWarNormalizationResult(
            WarApiWarNormalizationStatus.Normalized,
            canonical.NormalizationRun,
            canonical);
    }

    private async Task<WarApiWarNormalizationResult> RejectAsync(
        SourceParseRunId sourceParseRunId,
        DateTimeOffset startedAt,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var run = await normalization.RecordAsync(
            new NormalizationRunWrite(
                sourceParseRunId,
                WarApiVersions.WarNormalizer,
                NormalizationRunOutcome.Rejected,
                errorCode,
                startedAt,
                timeProvider.GetUtcNow()),
            cancellationToken);

        WarApiTelemetry.NormalizationRuns.Add(
            1,
            new KeyValuePair<string, object?>("source", WarApiCatalog.SourceKey),
            new KeyValuePair<string, object?>("domain", "war"),
            new KeyValuePair<string, object?>("outcome", "rejected"),
            new KeyValuePair<string, object?>("reason", errorCode));

        return new WarApiWarNormalizationResult(
            WarApiWarNormalizationStatus.Rejected,
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
                Math.Max(1d, decodedLength / (double)evidence.Body.Length));
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
                $"Durable representation for parse run {evidence.SourceParseRunId} can no longer be decoded: {exception.Message}");
        }
    }

    private static void ValidateReplayContext(
        CanonicalEvidenceInput evidence)
    {
        if (!string.Equals(
                evidence.SourceKey,
                WarApiCatalog.SourceKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.ParseCapabilityKey,
                WarApiCapabilities.RuntimeWarState.Key,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.EndpointCapabilityKey,
                WarApiCapabilities.RuntimeWarState.Key,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.ParseCapabilityKey,
                evidence.EndpointCapabilityKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.SemanticKey,
                "war",
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "War normalization was asked to consume evidence from a different source capability.");
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
                $"War normalizer {WarApiVersions.WarNormalizer} cannot replay adapter/parser identity " +
                $"'{evidence.AdapterVersion}/{evidence.ParserVersion}/{evidence.FingerprintAlgorithm}'.");
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
            WarApiParseOutcome.ParsedWithUnknowns => "parsed_with_unknowns",
            WarApiParseOutcome.MalformedJson => "malformed_json",
            WarApiParseOutcome.IncompatibleShape => "incompatible_shape",
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
            replay.UnknownPropertyCount != evidence.UnknownPropertyCount ||
            replay.UnknownCodeCount != evidence.UnknownCodeCount ||
            (evidence.DecodedByteLength is { } expectedLength &&
                expectedLength != decodedByteLength))
        {
            throw new CanonicalStateIntegrityException(
                $"Deterministic parser replay for source parse run {evidence.SourceParseRunId} did not match its durable parse metadata.");
        }
    }

    private static bool TryMapSnapshot(
        WarApiWarStateDto source,
        out CanonicalWarSnapshot? snapshot,
        out string? rejectionCode)
    {
        snapshot = null;
        rejectionCode = null;

        if (string.IsNullOrWhiteSpace(source.WarId) ||
            source.WarId.Length > 256 ||
            !string.Equals(
                source.WarId,
                source.WarId.Trim(),
                StringComparison.Ordinal))
        {
            rejectionCode = "invalid_source_war_id";
            return false;
        }

        if (source.WarNumber is < 0)
        {
            rejectionCode = "invalid_war_number";
            return false;
        }

        if (source.Winner is { Length: > 128 })
        {
            rejectionCode = "winner_too_long";
            return false;
        }

        if (source.RequiredVictoryTowns is < 0 ||
            source.ShortRequiredVictoryTowns is < 0)
        {
            rejectionCode = "negative_victory_towns";
            return false;
        }

        if (!TryUnixMilliseconds(
                source.ConquestStartTime,
                "invalid_conquest_start_time",
                out var conquestStartTime,
                out rejectionCode) ||
            !TryUnixMilliseconds(
                source.ConquestEndTime,
                "invalid_conquest_end_time",
                out var conquestEndTime,
                out rejectionCode) ||
            !TryUnixMilliseconds(
                source.ResistanceStartTime,
                "invalid_resistance_start_time",
                out var resistanceStartTime,
                out rejectionCode) ||
            !TryUnixMilliseconds(
                source.ScheduledConquestEndTime,
                "invalid_scheduled_conquest_end_time",
                out var scheduledConquestEndTime,
                out rejectionCode))
        {
            return false;
        }

        snapshot = new CanonicalWarSnapshot(
            source.WarId,
            source.WarNumber,
            source.Winner,
            conquestStartTime,
            conquestEndTime,
            resistanceStartTime,
            scheduledConquestEndTime,
            source.RequiredVictoryTowns,
            source.ShortRequiredVictoryTowns);

        return true;
    }

    private static bool TryUnixMilliseconds(
        long? value,
        string errorCode,
        out DateTimeOffset? timestamp,
        out string? rejectionCode)
    {
        timestamp = null;
        rejectionCode = null;

        if (value is null)
        {
            return true;
        }

        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(value.Value);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            rejectionCode = errorCode;
            return false;
        }
    }
}
