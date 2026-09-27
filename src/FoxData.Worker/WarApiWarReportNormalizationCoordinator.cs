using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Sources.WarApi;

namespace FoxData.Worker;

public enum WarApiWarReportNormalizationStatus
{
    Normalized,
    Rejected,
    Deferred,
}

public sealed record WarApiWarReportNormalizationResult(
    WarApiWarReportNormalizationStatus Status,
    NormalizationRunDescriptor? NormalizationRun,
    WarReportCanonicalResult? Canonical,
    string? DeferredReason = null);

public sealed class WarApiWarReportNormalizationCoordinator(
    ICanonicalEvidenceReader evidenceReader,
    WarApiMapContextResolver mapContextResolver,
    WarReportCanonicalKernel warReportCanonical,
    NormalizationKernel normalization,
    WarApiWorkerOptions options,
    TimeProvider timeProvider)
{
    private const string WarReportSemanticPrefix = "war-report/";
    private readonly WarApiParser _parser = new();

    public async Task<WarApiWarReportNormalizationResult> NormalizeAsync(
        SourceParseRunId sourceParseRunId,
        CancellationToken cancellationToken)
    {
        var existingRun = await normalization.GetAsync(
            sourceParseRunId,
            WarApiVersions.WarReportNormalizer,
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
            WarApiCapabilities.RegionWarReport,
            decoded);

        VerifyReplay(
            evidence,
            parsed,
            decoded.LongLength);

        if (parsed.Value is not WarApiWarReportDto source)
        {
            throw new CanonicalStateIntegrityException(
                "Successful War API replay did not produce a war-report DTO.");
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

        var mapContext = await mapContextResolver.ResolveAsync(
            evidence.ShardId,
            sourceMapName,
            evidence.RetrievedAt,
            cancellationToken);

        if (mapContext.Status == WarApiMapContextStatus.Deferred)
        {
            return Deferred(
                evidence,
                mapContext.Reason!);
        }

        if (mapContext.Status == WarApiMapContextStatus.Rejected)
        {
            return await RejectAsync(
                sourceParseRunId,
                startedAt,
                mapContext.Reason!,
                cancellationToken);
        }

        var warId = mapContext.WarId
            ?? throw new CanonicalStateIntegrityException(
                "Resolved map context did not provide a war identity.");
        var warRegion = mapContext.WarRegion
            ?? throw new CanonicalStateIntegrityException(
                "Resolved map context did not provide a war-region membership.");

        var completedAt = timeProvider.GetUtcNow();
        var canonical = await warReportCanonical.RecordAcceptedAsync(
            sourceParseRunId,
            WarApiVersions.WarReportNormalizer,
            WarApiCapabilities.RegionWarReport.Key,
            startedAt,
            completedAt,
            evidence.ShardId,
            evidence.RepresentationFetchId,
            warId,
            warRegion.Id,
            sourceMapName,
            evidence.RetrievedAt,
            snapshot!,
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
                "war-report"),
            new KeyValuePair<string, object?>(
                "outcome",
                "normalized"));

        return new WarApiWarReportNormalizationResult(
            WarApiWarReportNormalizationStatus.Normalized,
            canonical.NormalizationRun,
            canonical);
    }

    private async Task<WarApiWarReportNormalizationResult>
        ReplayTerminalAsync(
            NormalizationRunDescriptor existingRun,
            CancellationToken cancellationToken)
    {
        if (existingRun.Outcome == NormalizationRunOutcome.Rejected)
        {
            return new WarApiWarReportNormalizationResult(
                WarApiWarReportNormalizationStatus.Rejected,
                existingRun,
                null);
        }

        if (existingRun.Outcome != NormalizationRunOutcome.Normalized ||
            existingRun.ErrorCode is not null)
        {
            throw new CanonicalStateIntegrityException(
                $"War-report normalization run {existingRun.Id} has terminal outcome {existingRun.Outcome} and cannot be replayed as accepted work.");
        }

        var canonical =
            await warReportCanonical.GetByNormalizationRunAsync(
                existingRun.Id,
                cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"Normalized war-report run {existingRun.Id} has no durable canonical observation.");

        return new WarApiWarReportNormalizationResult(
            WarApiWarReportNormalizationStatus.Normalized,
            existingRun,
            canonical);
    }

    private static bool TryMapSnapshot(
        WarApiWarReportDto source,
        out CanonicalWarReportSnapshot? snapshot,
        out string? rejectionCode)
    {
        snapshot = null;
        rejectionCode = null;

        if (source.TotalEnlistments is < 0)
        {
            rejectionCode = "negative_total_enlistments";
            return false;
        }

        if (source.ColonialCasualties is < 0)
        {
            rejectionCode = "negative_colonial_casualties";
            return false;
        }

        if (source.WardenCasualties is < 0)
        {
            rejectionCode = "negative_warden_casualties";
            return false;
        }

        if (source.DayOfWar is < 0)
        {
            rejectionCode = "negative_day_of_war";
            return false;
        }

        snapshot = new CanonicalWarReportSnapshot(
            source.TotalEnlistments,
            source.ColonialCasualties,
            source.WardenCasualties,
            source.DayOfWar);
        return true;
    }

    private static WarApiWarReportNormalizationResult Deferred(
        CanonicalEvidenceInput evidence,
        string reason)
    {
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
                "war-report"),
            new KeyValuePair<string, object?>(
                "outcome",
                "deferred"),
            new KeyValuePair<string, object?>(
                "reason",
                reason));

        return new WarApiWarReportNormalizationResult(
            WarApiWarReportNormalizationStatus.Deferred,
            null,
            null,
            reason);
    }

    private async Task<WarApiWarReportNormalizationResult> RejectAsync(
        SourceParseRunId sourceParseRunId,
        DateTimeOffset startedAt,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var run = await normalization.RecordAsync(
            new NormalizationRunWrite(
                sourceParseRunId,
                WarApiVersions.WarReportNormalizer,
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
                "war-report"),
            new KeyValuePair<string, object?>(
                "outcome",
                "rejected"),
            new KeyValuePair<string, object?>(
                "reason",
                errorCode));

        return new WarApiWarReportNormalizationResult(
            WarApiWarReportNormalizationStatus.Rejected,
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
                $"Durable representation for parse run {evidence.SourceParseRunId} can no longer be decoded: {exception.Message}");
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
                WarApiCapabilities.RegionWarReport.Key,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.EndpointCapabilityKey,
                WarApiCapabilities.RegionWarReport.Key,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.ParseCapabilityKey,
                evidence.EndpointCapabilityKey,
                StringComparison.Ordinal) ||
            !evidence.SemanticKey.StartsWith(
                WarReportSemanticPrefix,
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "War-report normalization was asked to consume evidence from a different source capability.");
        }

        ValidateReplayVersions(
            evidence,
            WarApiVersions.WarReportNormalizer);

        var sourceMapName =
            evidence.SemanticKey[WarReportSemanticPrefix.Length..];

        try
        {
            return WarApiCatalog.ValidateMapName(sourceMapName);
        }
        catch (ArgumentException exception)
        {
            throw new CanonicalStateIntegrityException(
                $"War-report evidence has an invalid durable source map identity: {exception.Message}");
        }
    }

    private static void ValidateReplayVersions(
        CanonicalEvidenceInput evidence,
        string normalizerVersion)
    {
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
                $"War-report normalizer {normalizerVersion} cannot replay adapter/parser identity " +
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
}
