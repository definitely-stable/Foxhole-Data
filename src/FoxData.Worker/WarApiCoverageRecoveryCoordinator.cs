using System.Net;
using FoxData.Application.Canonical;
using FoxData.Application.Sources;
using FoxData.Core.Evidence;
using FoxData.Sources.WarApi;

namespace FoxData.Worker;

public sealed record WarApiCoverageRecoveryResult(
    int CoverageRecorded,
    int ParseRunsRepaired,
    int ContinuityApplied,
    int ContinuityRejected,
    int CanonicalCompleted,
    int CanonicalDeferred)
{
    public int ProgressCount =>
        CoverageRecorded +
        ParseRunsRepaired +
        ContinuityApplied +
        ContinuityRejected +
        CanonicalCompleted;
}

public sealed class WarApiCoverageRecoveryCoordinator(
    ICoverageStore coverageStore,
    ISourceParseRunStore parseRunStore,
    IWarContextReader warContextReader,
    WarApiWarNormalizationCoordinator warNormalization,
    WarApiRegionNormalizationCoordinator regionNormalization,
    WarApiWarReportNormalizationCoordinator warReportNormalization,
    WarApiStaticMapNormalizationCoordinator staticMapNormalization,
    WarApiDynamicMapNormalizationCoordinator dynamicMapNormalization,
    WarApiWorkerOptions options,
    TimeProvider timeProvider)
{
    private const int BatchSize = 64;
    private readonly WarApiParser _parser = new();

    public async Task<WarApiCoverageRecoveryResult> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var coverageRecorded = 0;
        var parseRunsRepaired = 0;
        var continuityApplied = 0;
        var continuityRejected = 0;
        var canonicalCompleted = 0;
        var canonicalDeferred = 0;

        var uncovered = await coverageStore.GetUncoveredAttemptsAsync(
            WarApiCatalog.SourceKey,
            WarApiCoverageCapabilityPlans.CoverageAndParse,
            BatchSize,
            cancellationToken);
        var repairedParseRuns = new Dictionary<
            (FetchId RepresentationFetchId, string CapabilityKey),
            SourceParseRunDescriptor>();

        foreach (var candidate in uncovered)
        {
            SourceParseRunDescriptor? parseRun = null;
            if (candidate.SourceParseRunId is null)
            {
                if (candidate.RepresentationFetchId is { } representationFetchId)
                {
                    var repairKey = (
                        representationFetchId,
                        candidate.CapabilityKey);
                    if (!repairedParseRuns.TryGetValue(
                            repairKey,
                            out parseRun))
                    {
                        parseRun = await RepairParseIfPossibleAsync(
                            candidate,
                            cancellationToken);
                        if (parseRun is not null)
                        {
                            repairedParseRuns.Add(
                                repairKey,
                                parseRun);
                            parseRunsRepaired++;
                        }
                    }
                }
                else
                {
                    parseRun = await RepairParseIfPossibleAsync(
                        candidate,
                        cancellationToken);
                }
            }

            var effectiveParseRunId =
                candidate.SourceParseRunId ??
                parseRun?.Id;
            var effectiveParseOutcome =
                candidate.ParseOutcome ??
                parseRun?.Outcome;
            var effectiveParseError =
                candidate.ParseErrorCode ??
                parseRun?.ErrorCode;

            var classification = Classify(
                candidate,
                effectiveParseRunId,
                effectiveParseOutcome,
                effectiveParseError);

            await coverageStore.RecordAsync(
                classification,
                cancellationToken);
            coverageRecorded++;
        }

        var continuityCandidates =
            await coverageStore.GetPendingMapContinuityAsync(
                WarApiCatalog.SourceKey,
                WarApiVersions.CoverageReprocessor,
                BatchSize,
                cancellationToken);

        foreach (var candidate in continuityCandidates)
        {
            var result = await ApplyMapContinuityAsync(
                candidate,
                cancellationToken);

            switch (result)
            {
                case ContinuityDisposition.Applied:
                    continuityApplied++;
                    break;
                case ContinuityDisposition.Rejected:
                    continuityRejected++;
                    break;
                case ContinuityDisposition.Deferred:
                    canonicalDeferred++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(result),
                        result,
                        "Unknown continuity disposition.");
            }
        }

        var canonicalCandidates =
            await coverageStore.GetPendingCanonicalReprocessingAsync(
                WarApiCatalog.SourceKey,
                WarApiCoverageCapabilityPlans.CanonicalNormalization,
                BatchSize,
                cancellationToken);

        foreach (var candidate in canonicalCandidates)
        {
            var disposition = await ReprocessCanonicalAsync(
                candidate,
                cancellationToken);

            if (disposition == CanonicalDisposition.Completed)
            {
                canonicalCompleted++;
            }
            else
            {
                canonicalDeferred++;
            }
        }

        return new WarApiCoverageRecoveryResult(
            coverageRecorded,
            parseRunsRepaired,
            continuityApplied,
            continuityRejected,
            canonicalCompleted,
            canonicalDeferred);
    }

    private async Task<SourceParseRunDescriptor?> RepairParseIfPossibleAsync(
        CoverageAttemptEvidence candidate,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                candidate.AttemptOutcomeCode,
                "captured_current",
                StringComparison.Ordinal) ||
            candidate.StatusCode is not (
                (int)HttpStatusCode.OK or
                (int)HttpStatusCode.NotModified) ||
            candidate.BodyErrorCode is not null ||
            candidate.RepresentationFetchId is null ||
            candidate.RepresentationBody is null)
        {
            return null;
        }

        var endpoint = WarApiCatalog.FromRegistry(
            candidate.CapabilityKey,
            candidate.SemanticKey);
        var startedAt = timeProvider.GetUtcNow();

        WarApiParseResult? parsed = null;
        string outcome;
        string? fingerprint = null;
        var unknownProperties = 0;
        var unknownCodes = 0;
        string? errorCode = null;
        long? decodedByteLength = null;

        try
        {
            var decoded = await WarApiContentPolicy.DecodeAsync(
                candidate.RepresentationBody,
                candidate.ContentEncoding,
                options.MaxDecodedBytes,
                options.MaxExpansionRatio,
                cancellationToken);

            decodedByteLength = decoded.LongLength;
            parsed = _parser.Parse(
                endpoint.Capability,
                decoded);

            outcome = ToParseOutcome(parsed.Outcome);
            fingerprint = parsed.StructuralFingerprint;
            unknownProperties = parsed.UnknownPropertyCount;
            unknownCodes = parsed.UnknownCodeCount;
            errorCode = parsed.ErrorCode;
        }
        catch (WarApiDecodingException)
        {
            outcome = "content_decode_failed";
            errorCode = "content_decode_failed";
        }

        return await parseRunStore.RecordAsync(
            new SourceParseRunWrite(
                candidate.RepresentationFetchId.Value,
                candidate.CapabilityKey,
                WarApiVersions.Adapter,
                WarApiVersions.Parser,
                JsonStructuralFingerprinter.Algorithm,
                fingerprint,
                outcome,
                unknownProperties,
                unknownCodes,
                errorCode,
                startedAt,
                timeProvider.GetUtcNow(),
                parsed?.SourceVersion,
                parsed?.SourceLastUpdated,
                decodedByteLength),
            cancellationToken);
    }

    private static CoverageObservationWrite Classify(
        CoverageAttemptEvidence candidate,
        SourceParseRunId? parseRunId,
        string? parseOutcome,
        string? parseErrorCode)
    {
        var boundaryAt =
            candidate.RetrievedAt ??
            candidate.AttemptCompletedAt ??
            candidate.AttemptStartedAt;

        if (string.Equals(
                candidate.AttemptOutcomeCode,
                "abandoned_before_exchange",
                StringComparison.Ordinal))
        {
            return Create(
                candidate,
                parseRunId: null,
                CoverageState.CollectorUnavailable,
                boundaryAt,
                candidate.AttemptErrorCode ??
                    "abandoned_before_exchange");
        }

        if (!string.Equals(
                candidate.AttemptOutcomeCode,
                "captured_current",
                StringComparison.Ordinal))
        {
            return Create(
                candidate,
                parseRunId,
                CoverageState.Unknown,
                boundaryAt,
                candidate.AttemptOutcomeCode ??
                    candidate.AttemptState);
        }

        if (candidate.ValidationFetchId is null)
        {
            return Create(
                candidate,
                parseRunId: null,
                CoverageState.Unknown,
                boundaryAt,
                "current_fetch_missing");
        }

        if (candidate.BodyErrorCode is not null)
        {
            return Create(
                candidate,
                parseRunId: null,
                CoverageState.CollectorUnavailable,
                boundaryAt,
                candidate.BodyErrorCode);
        }

        if (candidate.StatusCode == (int)HttpStatusCode.OK)
        {
            if (candidate.RepresentationFetchId !=
                    candidate.ValidationFetchId ||
                candidate.RepresentationBody is null)
            {
                return Create(
                    candidate,
                    parseRunId,
                    CoverageState.Unknown,
                    boundaryAt,
                    "body_representation_missing");
            }

            if (parseRunId is null || parseOutcome is null)
            {
                return Create(
                    candidate,
                    parseRunId: null,
                    CoverageState.Unknown,
                    boundaryAt,
                    "source_parse_missing");
            }

            return IsSuccessfulParse(parseOutcome)
                ? Create(
                    candidate,
                    parseRunId,
                    CoverageState.Observed,
                    boundaryAt,
                    detailCode: null)
                : Create(
                    candidate,
                    parseRunId,
                    CoverageState.Rejected,
                    boundaryAt,
                    parseErrorCode ?? parseOutcome);
        }

        if (candidate.StatusCode ==
            (int)HttpStatusCode.NotModified)
        {
            if (candidate.RepresentationFetchId is null ||
                candidate.RepresentationFetchId ==
                    candidate.ValidationFetchId ||
                candidate.RepresentationBody is null)
            {
                return Create(
                    candidate,
                    parseRunId: null,
                    CoverageState.Unknown,
                    boundaryAt,
                    "orphan_304");
            }

            if (parseRunId is null || parseOutcome is null)
            {
                return Create(
                    candidate,
                    parseRunId: null,
                    CoverageState.Unknown,
                    boundaryAt,
                    "representation_parse_missing");
            }

            return IsSuccessfulParse(parseOutcome)
                ? Create(
                    candidate,
                    parseRunId,
                    CoverageState.SourceNotModified,
                    boundaryAt,
                    detailCode: null)
                : Create(
                    candidate,
                    parseRunId,
                    CoverageState.Rejected,
                    boundaryAt,
                    parseErrorCode ?? parseOutcome);
        }

        if (candidate.StatusCode is not null)
        {
            return Create(
                candidate,
                parseRunId: null,
                CoverageState.SourceUnavailable,
                boundaryAt,
                $"http_{candidate.StatusCode.Value}");
        }

        return Create(
            candidate,
            parseRunId: null,
            CoverageState.Unknown,
            boundaryAt,
            "http_status_unknown");
    }

    private static CoverageObservationWrite Create(
        CoverageAttemptEvidence candidate,
        SourceParseRunId? parseRunId,
        CoverageState state,
        DateTimeOffset boundaryAt,
        string? detailCode) =>
        new(
            candidate.EndpointId,
            candidate.CollectionJobId,
            candidate.AttemptId,
            candidate.ValidationFetchId,
            state is CoverageState.Observed or
                CoverageState.SourceNotModified or
                CoverageState.Rejected
                ? candidate.RepresentationFetchId
                : null,
            state is CoverageState.Observed or
                CoverageState.SourceNotModified or
                CoverageState.Rejected
                ? parseRunId
                : null,
            state,
            boundaryAt,
            NormalizeDetailCode(detailCode));

    private async Task<ContinuityDisposition> ApplyMapContinuityAsync(
        CoverageContinuityCandidate candidate,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();

        ValidateReplayIdentity(candidate);

        var decoded = await DecodeContinuityRepresentationAsync(
            candidate,
            cancellationToken);
        var parsed = _parser.Parse(
            WarApiCapabilities.ActiveMapList,
            decoded);

        VerifyContinuityReplay(
            candidate,
            parsed,
            decoded.LongLength);

        if (parsed.Value is not string[] sourceMapNames)
        {
            throw new CanonicalStateIntegrityException(
                "Successful coverage replay did not produce an active-map-list array.");
        }

        var memberships = new List<RegionMembershipCandidate>();
        foreach (var sourceMapName in sourceMapNames
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            string validated;
            try
            {
                validated =
                    WarApiCatalog.ValidateMapName(sourceMapName);
            }
            catch (ArgumentException)
            {
                await coverageStore.RecordMapContinuityRejectedAsync(
                    candidate.Coverage.Id,
                    WarApiVersions.CoverageReprocessor,
                    startedAt,
                    timeProvider.GetUtcNow(),
                    "invalid_source_map_name",
                    cancellationToken);
                return ContinuityDisposition.Rejected;
            }

            memberships.Add(
                new RegionMembershipCandidate(
                    BuildCanonicalRegionKey(
                        candidate.SourceKey,
                        validated),
                    validated,
                    validated));
        }

        var warContext = await warContextReader.GetAtOrBeforeAsync(
            candidate.ShardId,
            candidate.Coverage.BoundaryAt,
            WarApiCapabilities.RuntimeWarState.Key,
            "war",
            WarApiVersions.Parser,
            cancellationToken);

        if (warContext is null ||
            warContext.StatusCode is not (
                (int)HttpStatusCode.OK or
                (int)HttpStatusCode.NotModified) ||
            warContext.RepresentationFetchId is null ||
            warContext.SourceParseRunId is null)
        {
            return ContinuityDisposition.Deferred;
        }

        var warResult = await warNormalization.NormalizeAsync(
            warContext.SourceParseRunId.Value,
            cancellationToken);

        if (warResult.Status !=
                WarApiWarNormalizationStatus.Normalized ||
            warResult.Canonical is null)
        {
            await coverageStore.RecordMapContinuityRejectedAsync(
                candidate.Coverage.Id,
                WarApiVersions.CoverageReprocessor,
                startedAt,
                timeProvider.GetUtcNow(),
                "war_context_rejected",
                cancellationToken);
            return ContinuityDisposition.Rejected;
        }

        await coverageStore.RecordMapContinuityAsync(
            new CoverageContinuityWrite(
                candidate.Coverage.Id,
                WarApiVersions.CoverageReprocessor,
                startedAt,
                timeProvider.GetUtcNow(),
                candidate.ShardId,
                warResult.Canonical.War.Id,
                candidate.Coverage.BoundaryAt,
                memberships),
            cancellationToken);

        return ContinuityDisposition.Applied;
    }

    private async Task<CanonicalDisposition> ReprocessCanonicalAsync(
        CanonicalReprocessingCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
                candidate.CapabilityKey,
                WarApiCapabilities.RuntimeWarState.Key,
                StringComparison.Ordinal))
        {
            await warNormalization.NormalizeAsync(
                candidate.SourceParseRunId,
                cancellationToken);
            return CanonicalDisposition.Completed;
        }

        if (string.Equals(
                candidate.CapabilityKey,
                WarApiCapabilities.ActiveMapList.Key,
                StringComparison.Ordinal))
        {
            var result = await regionNormalization.NormalizeAsync(
                candidate.SourceParseRunId,
                cancellationToken);
            return result.Status ==
                WarApiRegionNormalizationStatus.Deferred
                ? CanonicalDisposition.Deferred
                : CanonicalDisposition.Completed;
        }

        if (string.Equals(
                candidate.CapabilityKey,
                WarApiCapabilities.RegionWarReport.Key,
                StringComparison.Ordinal))
        {
            var result = await warReportNormalization.NormalizeAsync(
                candidate.SourceParseRunId,
                cancellationToken);
            return result.Status ==
                WarApiWarReportNormalizationStatus.Deferred
                ? CanonicalDisposition.Deferred
                : CanonicalDisposition.Completed;
        }

        if (string.Equals(
                candidate.CapabilityKey,
                WarApiCapabilities.StaticMapState.Key,
                StringComparison.Ordinal))
        {
            await staticMapNormalization.NormalizeAsync(
                candidate.SourceParseRunId,
                cancellationToken);
            return CanonicalDisposition.Completed;
        }

        if (string.Equals(
                candidate.CapabilityKey,
                WarApiCapabilities.DynamicMapState.Key,
                StringComparison.Ordinal))
        {
            await dynamicMapNormalization.NormalizeAsync(
                candidate.SourceParseRunId,
                cancellationToken);
            return CanonicalDisposition.Completed;
        }

        throw new CanonicalStateIntegrityException(
            $"Unsupported canonical reprocessing capability '{candidate.CapabilityKey}'.");
    }

    private async Task<byte[]> DecodeContinuityRepresentationAsync(
        CoverageContinuityCandidate candidate,
        CancellationToken cancellationToken)
    {
        var maximumDecodedBytes = options.MaxDecodedBytes;
        if (candidate.DecodedByteLength is { } durableLength)
        {
            if (durableLength is < 0 or > int.MaxValue)
            {
                throw new CanonicalStateIntegrityException(
                    "Coverage representation has an invalid decoded byte length.");
            }

            maximumDecodedBytes = Math.Max(
                maximumDecodedBytes,
                (int)durableLength);
        }

        var maximumExpansionRatio = options.MaxExpansionRatio;
        if (candidate.DecodedByteLength is { } decodedLength &&
            candidate.RepresentationBody.Length > 0)
        {
            maximumExpansionRatio = Math.Max(
                maximumExpansionRatio,
                Math.Max(
                    1d,
                    decodedLength /
                    (double)candidate.RepresentationBody.Length));
        }

        try
        {
            return await WarApiContentPolicy.DecodeAsync(
                candidate.RepresentationBody,
                candidate.ContentEncoding,
                maximumDecodedBytes,
                maximumExpansionRatio,
                cancellationToken);
        }
        catch (WarApiDecodingException exception)
        {
            throw new CanonicalStateIntegrityException(
                $"Durable coverage representation can no longer be decoded: {exception.Message}");
        }
    }

    private static void ValidateReplayIdentity(
        CoverageContinuityCandidate candidate)
    {
        if (!string.Equals(
                candidate.SourceKey,
                WarApiCatalog.SourceKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.CapabilityKey,
                WarApiCapabilities.ActiveMapList.Key,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.SemanticKey,
                "maps",
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.AdapterVersion,
                WarApiVersions.Adapter,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.ParserVersion,
                WarApiVersions.Parser,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.FingerprintAlgorithm,
                JsonStructuralFingerprinter.Algorithm,
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Coverage continuity candidate has incompatible source/parser provenance.");
        }
    }

    private static void VerifyContinuityReplay(
        CoverageContinuityCandidate candidate,
        WarApiParseResult replay,
        long decodedByteLength)
    {
        var replayOutcome = ToParseOutcome(replay.Outcome);

        if (!replay.Parsed ||
            !string.Equals(
                replayOutcome,
                candidate.ParseOutcome,
                StringComparison.Ordinal) ||
            !string.Equals(
                replay.StructuralFingerprint,
                candidate.StructuralFingerprint,
                StringComparison.Ordinal) ||
            replay.UnknownPropertyCount !=
                candidate.UnknownPropertyCount ||
            replay.UnknownCodeCount != candidate.UnknownCodeCount ||
            (candidate.DecodedByteLength is { } expectedLength &&
                expectedLength != decodedByteLength))
        {
            throw new CanonicalStateIntegrityException(
                "Deterministic coverage replay did not match durable source-parse metadata.");
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

    private static string? NormalizeDetailCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 128
            ? trimmed
            : trimmed[..128];
    }

    private static bool IsSuccessfulParse(string outcome) =>
        outcome is "parsed" or "parsed_with_unknowns";

    private static string ToParseOutcome(
        WarApiParseOutcome outcome) =>
        outcome switch
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

    private enum ContinuityDisposition
    {
        Applied,
        Rejected,
        Deferred,
    }

    private enum CanonicalDisposition
    {
        Completed,
        Deferred,
    }
}
