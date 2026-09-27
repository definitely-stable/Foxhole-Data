using System.Net;
using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Sources.WarApi;

namespace FoxData.Worker;

public enum WarApiRegionNormalizationStatus
{
    Normalized,
    Rejected,
    Deferred,
}

public sealed record WarApiRegionNormalizationResult(
    WarApiRegionNormalizationStatus Status,
    NormalizationRunDescriptor? NormalizationRun,
    RegionCanonicalResult? Canonical,
    string? DeferredReason = null);

public sealed class WarApiRegionNormalizationCoordinator(
    ICanonicalEvidenceReader evidenceReader,
    IWarContextReader warContextReader,
    WarApiWarNormalizationCoordinator warNormalization,
    RegionCanonicalKernel regionCanonical,
    NormalizationKernel normalization,
    WarApiWorkerOptions options,
    TimeProvider timeProvider)
{
    private readonly WarApiParser _parser = new();

    public async Task<WarApiRegionNormalizationResult> NormalizeAsync(
        SourceParseRunId sourceParseRunId,
        CancellationToken cancellationToken)
    {
        var existingRun = await normalization.GetAsync(
            sourceParseRunId,
            WarApiVersions.RegionNormalizer,
            cancellationToken);

        if (existingRun is not null)
        {
            return existingRun.Outcome switch
            {
                NormalizationRunOutcome.Normalized =>
                    new WarApiRegionNormalizationResult(
                        WarApiRegionNormalizationStatus.Normalized,
                        existingRun,
                        null),
                NormalizationRunOutcome.Rejected =>
                    new WarApiRegionNormalizationResult(
                        WarApiRegionNormalizationStatus.Rejected,
                        existingRun,
                        null),
                _ => throw new CanonicalStateIntegrityException(
                    $"Region normalization run {existingRun.Id} has terminal outcome {existingRun.Outcome} and cannot be replayed as accepted work."),
            };
        }

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
            WarApiCapabilities.ActiveMapList,
            decoded);

        VerifyReplay(evidence, parsed, decoded.LongLength);

        if (parsed.Value is not string[] sourceMapNames)
        {
            throw new CanonicalStateIntegrityException(
                "Successful War API replay did not produce an active-map-list DTO.");
        }

        var memberships = new List<RegionMembershipCandidate>();
        foreach (var sourceMapName in sourceMapNames
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            string validated;
            try
            {
                validated = WarApiCatalog.ValidateMapName(sourceMapName);
            }
            catch (ArgumentException)
            {
                return await RejectAsync(
                    sourceParseRunId,
                    startedAt,
                    "invalid_source_map_name",
                    cancellationToken);
            }

            memberships.Add(
                new RegionMembershipCandidate(
                    BuildCanonicalRegionKey(
                        evidence.SourceKey,
                        validated),
                    validated,
                    validated));
        }

        var sourceWarContext = await warContextReader.GetAtOrBeforeAsync(
            evidence.ShardId,
            evidence.RetrievedAt,
            WarApiCapabilities.RuntimeWarState.Key,
            "war",
            WarApiVersions.Parser,
            cancellationToken);

        if (sourceWarContext is null)
        {
            return Deferred(
                evidence,
                "war_evidence_unavailable");
        }

        if (sourceWarContext.StatusCode is not (
                (int)HttpStatusCode.OK or
                (int)HttpStatusCode.NotModified) ||
            sourceWarContext.RepresentationFetchId is null)
        {
            return Deferred(
                evidence,
                "war_context_unconfirmed");
        }

        if (sourceWarContext.SourceParseRunId is null)
        {
            return Deferred(
                evidence,
                "war_parse_unavailable");
        }

        var warResult = await warNormalization.NormalizeAsync(
            sourceWarContext.SourceParseRunId.Value,
            cancellationToken);

        if (warResult.Status != WarApiWarNormalizationStatus.Normalized ||
            warResult.Canonical is null)
        {
            return await RejectAsync(
                sourceParseRunId,
                startedAt,
                "war_context_rejected",
                cancellationToken);
        }

        var completedAt = timeProvider.GetUtcNow();
        var canonical = await regionCanonical.RecordAcceptedAsync(
            sourceParseRunId,
            WarApiVersions.RegionNormalizer,
            WarApiCapabilities.ActiveMapList.Key,
            startedAt,
            completedAt,
            evidence.ShardId,
            evidence.RepresentationFetchId,
            warResult.Canonical.War.Id,
            evidence.RetrievedAt,
            memberships,
            cancellationToken);

        WarApiTelemetry.NormalizationRuns.Add(
            1,
            new KeyValuePair<string, object?>("source", WarApiCatalog.SourceKey),
            new KeyValuePair<string, object?>("environment", evidence.Environment),
            new KeyValuePair<string, object?>("shard", evidence.ShardKey),
            new KeyValuePair<string, object?>("domain", "region-membership"),
            new KeyValuePair<string, object?>("outcome", "normalized"));

        return new WarApiRegionNormalizationResult(
            WarApiRegionNormalizationStatus.Normalized,
            canonical.NormalizationRun,
            canonical);
    }

    private static WarApiRegionNormalizationResult Deferred(
        CanonicalEvidenceInput evidence,
        string reason)
    {
        WarApiTelemetry.NormalizationRuns.Add(
            1,
            new KeyValuePair<string, object?>("source", WarApiCatalog.SourceKey),
            new KeyValuePair<string, object?>("environment", evidence.Environment),
            new KeyValuePair<string, object?>("shard", evidence.ShardKey),
            new KeyValuePair<string, object?>("domain", "region-membership"),
            new KeyValuePair<string, object?>("outcome", "deferred"),
            new KeyValuePair<string, object?>("reason", reason));

        return new WarApiRegionNormalizationResult(
            WarApiRegionNormalizationStatus.Deferred,
            null,
            null,
            reason);
    }

    private async Task<WarApiRegionNormalizationResult> RejectAsync(
        SourceParseRunId sourceParseRunId,
        DateTimeOffset startedAt,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var run = await normalization.RecordAsync(
            new NormalizationRunWrite(
                sourceParseRunId,
                WarApiVersions.RegionNormalizer,
                NormalizationRunOutcome.Rejected,
                errorCode,
                startedAt,
                timeProvider.GetUtcNow()),
            cancellationToken);

        WarApiTelemetry.NormalizationRuns.Add(
            1,
            new KeyValuePair<string, object?>("source", WarApiCatalog.SourceKey),
            new KeyValuePair<string, object?>("domain", "region-membership"),
            new KeyValuePair<string, object?>("outcome", "rejected"),
            new KeyValuePair<string, object?>("reason", errorCode));

        return new WarApiRegionNormalizationResult(
            WarApiRegionNormalizationStatus.Rejected,
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
                WarApiCapabilities.ActiveMapList.Key,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.EndpointCapabilityKey,
                WarApiCapabilities.ActiveMapList.Key,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.ParseCapabilityKey,
                evidence.EndpointCapabilityKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.SemanticKey,
                "maps",
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Region normalization was asked to consume evidence from a different source capability.");
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
                $"Region normalizer {WarApiVersions.RegionNormalizer} cannot replay adapter/parser identity " +
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

    private static string BuildCanonicalRegionKey(
        string sourceKey,
        string sourceMapName)
    {
        var key = $"{sourceKey}/map/{sourceMapName}";
        if (key.Length > 256)
        {
            throw new CanonicalStateIntegrityException(
                "Source-scoped canonical region key exceeds the M5 storage bound.");
        }

        return key;
    }
}
