using System.Net;
using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Core.Runtime;
using FoxData.Core.Sources;
using FoxData.Sources.WarApi;

namespace FoxData.Worker;

public enum WarApiMapContextStatus
{
    Resolved,
    Rejected,
    Deferred,
}

public sealed record WarApiMapContextResolution(
    WarApiMapContextStatus Status,
    WarId? WarId,
    WarRegionDescriptor? WarRegion,
    WarSourceContextDescriptor? WarSourceContext,
    WarSourceContextDescriptor? MapListSourceContext,
    bool UsedValidatedContinuity,
    string? Reason)
{
    public static WarApiMapContextResolution Resolved(
        WarId warId,
        WarRegionDescriptor warRegion,
        WarSourceContextDescriptor warSourceContext,
        WarSourceContextDescriptor mapListSourceContext,
        bool usedValidatedContinuity) =>
        new(
            WarApiMapContextStatus.Resolved,
            warId,
            warRegion,
            warSourceContext,
            mapListSourceContext,
            usedValidatedContinuity,
            null);

    public static WarApiMapContextResolution Rejected(
        string reason,
        WarSourceContextDescriptor? warSourceContext = null,
        WarSourceContextDescriptor? mapListSourceContext = null) =>
        new(
            WarApiMapContextStatus.Rejected,
            null,
            null,
            warSourceContext,
            mapListSourceContext,
            false,
            reason);

    public static WarApiMapContextResolution Deferred(
        string reason,
        WarSourceContextDescriptor? warSourceContext = null,
        WarSourceContextDescriptor? mapListSourceContext = null) =>
        new(
            WarApiMapContextStatus.Deferred,
            null,
            null,
            warSourceContext,
            mapListSourceContext,
            false,
            reason);
}

public sealed class WarApiMapContextResolver(
    IWarContextReader sourceContextReader,
    ICanonicalEvidenceReader evidenceReader,
    IWarRegionReader warRegionReader,
    ICoverageStore coverageStore,
    WarApiWarNormalizationCoordinator warNormalization,
    WarApiRegionNormalizationCoordinator regionNormalization,
    WarApiWorkerOptions options)
{
    private readonly WarApiParser _parser = new();

    public async Task<WarApiMapContextResolution> ResolveAsync(
        ShardId shardId,
        string sourceMapName,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            sourceMapName =
                WarApiCatalog.ValidateMapName(sourceMapName);
        }
        catch (ArgumentException exception)
        {
            throw new CanonicalStateIntegrityException(
                $"Map context resolution received an invalid source map identity: {exception.Message}");
        }

        var targetWar = await ResolveWarAsync(
            shardId,
            observedAt,
            cancellationToken);

        if (targetWar.Status != WarApiMapContextStatus.Resolved)
        {
            return targetWar;
        }

        var mapsContext = await sourceContextReader.GetAtOrBeforeAsync(
            shardId,
            observedAt,
            WarApiCapabilities.ActiveMapList.Key,
            "maps",
            WarApiVersions.Parser,
            cancellationToken);

        if (mapsContext is null)
        {
            return WarApiMapContextResolution.Deferred(
                "map_list_evidence_unavailable",
                targetWar.WarSourceContext);
        }

        if (mapsContext.StatusCode is not (
                (int)HttpStatusCode.OK or
                (int)HttpStatusCode.NotModified) ||
            mapsContext.RepresentationFetchId is null)
        {
            return WarApiMapContextResolution.Deferred(
                "map_list_context_unconfirmed",
                targetWar.WarSourceContext,
                mapsContext);
        }

        if (mapsContext.SourceParseRunId is null)
        {
            return WarApiMapContextResolution.Deferred(
                "map_list_parse_unavailable",
                targetWar.WarSourceContext,
                mapsContext);
        }

        var mapsEvidence = await evidenceReader.GetAsync(
            mapsContext.SourceParseRunId.Value,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"Active-map-list parse run {mapsContext.SourceParseRunId.Value} has no replayable representation.");

        ValidateMapsReplayContext(mapsEvidence);

        if (mapsEvidence.ParseOutcome is not (
                "parsed" or "parsed_with_unknowns"))
        {
            return WarApiMapContextResolution.Rejected(
                "map_list_parse_unsuccessful",
                targetWar.WarSourceContext,
                mapsContext);
        }

        var mapsDecoded =
            await DecodeDurableRepresentationAsync(
                mapsEvidence,
                cancellationToken);
        var mapsParsed = _parser.Parse(
            WarApiCapabilities.ActiveMapList,
            mapsDecoded);

        VerifyReplay(
            mapsEvidence,
            mapsParsed,
            mapsDecoded.LongLength);

        if (mapsParsed.Value is not string[] sourceMapNames)
        {
            throw new CanonicalStateIntegrityException(
                "Successful active-map-list replay did not produce a map-name array.");
        }

        if (!sourceMapNames.Contains(
                sourceMapName,
                StringComparer.Ordinal))
        {
            return WarApiMapContextResolution.Deferred(
                "map_membership_unconfirmed",
                targetWar.WarSourceContext,
                mapsContext);
        }

        var usesValidatedContinuity =
            mapsContext.ValidationFetchId !=
            mapsContext.RepresentationFetchId;

        if (usesValidatedContinuity)
        {
            var continuityApplied =
                await coverageStore.IsMapContinuityAppliedAsync(
                    mapsContext.ValidationFetchId,
                    WarApiVersions.CoverageReprocessor,
                    cancellationToken);

            if (!continuityApplied)
            {
                return WarApiMapContextResolution.Deferred(
                    "map_continuity_requires_coverage",
                    targetWar.WarSourceContext,
                    mapsContext);
            }
        }
        else
        {
            var mapsWar = await ResolveWarAsync(
                shardId,
                mapsEvidence.RetrievedAt,
                cancellationToken);

            if (mapsWar.Status ==
                WarApiMapContextStatus.Deferred)
            {
                return WarApiMapContextResolution.Deferred(
                    mapsWar.Reason!,
                    targetWar.WarSourceContext,
                    mapsContext);
            }

            if (mapsWar.Status ==
                WarApiMapContextStatus.Rejected)
            {
                return WarApiMapContextResolution.Rejected(
                    "map_war_context_rejected",
                    targetWar.WarSourceContext,
                    mapsContext);
            }

            if (mapsWar.WarId != targetWar.WarId)
            {
                return WarApiMapContextResolution.Deferred(
                    "map_war_context_mismatch",
                    targetWar.WarSourceContext,
                    mapsContext);
            }

            var regionResult =
                await regionNormalization.NormalizeAsync(
                    mapsContext.SourceParseRunId.Value,
                    cancellationToken);

            if (regionResult.Status ==
                WarApiRegionNormalizationStatus.Deferred)
            {
                return WarApiMapContextResolution.Deferred(
                    "region_context_deferred",
                    targetWar.WarSourceContext,
                    mapsContext);
            }

            if (regionResult.Status ==
                WarApiRegionNormalizationStatus.Rejected)
            {
                return WarApiMapContextResolution.Rejected(
                    "region_context_rejected",
                    targetWar.WarSourceContext,
                    mapsContext);
            }
        }

        var warRegion = await warRegionReader.GetAsync(
            targetWar.WarId!.Value,
            sourceMapName,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"Proven active-map-list evidence contains '{sourceMapName}' but no matching war-region exists for war {targetWar.WarId.Value}.");

        return WarApiMapContextResolution.Resolved(
            targetWar.WarId.Value,
            warRegion,
            targetWar.WarSourceContext!,
            mapsContext,
            usesValidatedContinuity);
    }

    private async Task<WarApiMapContextResolution> ResolveWarAsync(
        ShardId shardId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var sourceWarContext =
            await sourceContextReader.GetAtOrBeforeAsync(
                shardId,
                observedAt,
                WarApiCapabilities.RuntimeWarState.Key,
                "war",
                WarApiVersions.Parser,
                cancellationToken);

        if (sourceWarContext is null)
        {
            return WarApiMapContextResolution.Deferred(
                "war_evidence_unavailable");
        }

        if (sourceWarContext.StatusCode is not (
                (int)HttpStatusCode.OK or
                (int)HttpStatusCode.NotModified) ||
            sourceWarContext.RepresentationFetchId is null)
        {
            return WarApiMapContextResolution.Deferred(
                "war_context_unconfirmed",
                sourceWarContext);
        }

        if (sourceWarContext.SourceParseRunId is null)
        {
            return WarApiMapContextResolution.Deferred(
                "war_parse_unavailable",
                sourceWarContext);
        }

        var warResult = await warNormalization.NormalizeAsync(
            sourceWarContext.SourceParseRunId.Value,
            cancellationToken);

        if (warResult.Status !=
                WarApiWarNormalizationStatus.Normalized ||
            warResult.Canonical is null)
        {
            return WarApiMapContextResolution.Rejected(
                "war_context_rejected",
                sourceWarContext);
        }

        return new WarApiMapContextResolution(
            WarApiMapContextStatus.Resolved,
            warResult.Canonical.War.Id,
            null,
            sourceWarContext,
            null,
            false,
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
                    decodedLength /
                    (double)evidence.Body.Length));
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
                $"Durable active-map-list representation for parse run {evidence.SourceParseRunId} can no longer be decoded: {exception.Message}");
        }
    }

    private static void ValidateMapsReplayContext(
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
                "Map context resolution found invalid active-map-list provenance.");
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
                "Map context resolution cannot replay an incompatible adapter/parser identity.");
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
