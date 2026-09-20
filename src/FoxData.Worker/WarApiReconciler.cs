using System.Net;
using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
using FoxData.Application.Sources;
using FoxData.Core.Evidence;
using FoxData.Core.Sources;
using FoxData.Sources.Abstractions;
using FoxData.Sources.WarApi;

namespace FoxData.Worker;

public sealed class WarApiReconciler(
    WarApiRegistryResolver resolver,
    IEndpointEvidenceReader evidenceReader,
    IEndpointPollStateStore pollStateStore,
    ISourceParseRunStore parseRunStore,
    ISourceScheduleDecisionStore scheduleDecisionStore,
    SourceRegistry registry,
    IngestionKernel ingestion,
    WarApiWorkerOptions options,
    WarApiCollectionProfile collectionProfile,
    WarApiMeasurementProbeProfile measurementProbe,
    TimeProvider timeProvider,
    ILogger<WarApiReconciler> logger)
{
    private readonly WarApiParser _parser = new();
    private readonly WarApiCachePolicy _cachePolicy = new();

    public async Task ReconcileAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken)
    {
        var context = await resolver.ResolveAsync(endpointId, cancellationToken);
        var snapshot = await evidenceReader.GetCurrentAsync(endpointId, cancellationToken);
        if (snapshot is null)
        {
            return;
        }

        var previousPoll = await pollStateStore.GetAsync(
            endpointId,
            cancellationToken);

        if (previousPoll?.LastProcessedFetchId == snapshot.CurrentFetch.Id)
        {
            return;
        }

        WarApiParseResult? parsed = null;
        if (snapshot.CurrentFetch.StatusCode == (int)HttpStatusCode.OK &&
            snapshot.CurrentFetch.PayloadId is not null &&
            snapshot.RepresentationPayload is not null)
        {
            parsed = await ParseAndRecordAsync(
                context,
                snapshot.CurrentFetch,
                snapshot.RepresentationPayload,
                cancellationToken);

            if (context.SourceEndpoint.Capability ==
                    WarApiCapabilities.ActiveMapList &&
                parsed?.Parsed is true &&
                parsed.Value is string[] maps)
            {
                await ReconcileMapDiscoveryAsync(
                    context,
                    maps,
                    cancellationToken);
            }
        }

        var activity = await ResolveEndpointActivityAsync(
            context,
            cancellationToken);

        var baseCadence = collectionProfile.TargetCadence(
            context.SourceEndpoint.Capability);
        var activeMapNames =
            activity.ActiveMapNames ??
            Array.Empty<string>();
        var probeSelected =
            activity.ActiveMapNames is not null &&
            WarApiMeasurementProbePolicy.IsSelectedEndpoint(
                measurementProbe,
                context.Shard.Key,
                context.SourceEndpoint,
                activeMapNames);
        var cadence =
            WarApiMeasurementProbePolicy.ResolveCadence(
                measurementProbe,
                context.Shard.Key,
                context.SourceEndpoint,
                activeMapNames,
                baseCadence);
        var schedulingPolicy =
            WarApiMeasurementProbePolicy.SchedulingPolicy(
                WarApiVersions.SchedulingPolicy(collectionProfile),
                measurementProbe,
                probeSelected);

        var transition = BuildPollTransition(
            context,
            snapshot,
            previousPoll,
            activity.Active,
            cadence,
            schedulingPolicy);

        CollectionJobDescriptor? successorJob = null;
        if (transition.SuccessorAvailableAt is { } successorAvailableAt)
        {
            var successor = await ingestion.EnqueueAsync(
                endpointId,
                $"after-fetch:{snapshot.CurrentFetch.Id}",
                snapshot.CurrentFetch.RetrievedAt,
                successorAvailableAt,
                cancellationToken: cancellationToken);

            if (successor.Status is JobEnqueueStatus.Conflict)
            {
                throw new SourceStateIntegrityException(
                    $"Successor job for fetch {snapshot.CurrentFetch.Id} conflicts with durable scheduling.");
            }

            successorJob = successor.Job;
        }

        await scheduleDecisionStore.RecordAsync(
            new SourceScheduleDecisionWrite(
                snapshot.CurrentFetch.Id,
                context.Endpoint.Id,
                schedulingPolicy,
                checked((long)cadence.TotalMilliseconds),
                activity.Active,
                probeSelected,
                transition.State.SourceCacheEligibleAt,
                transition.State.NextTargetAt,
                transition.State.RetryEligibleAt,
                successorJob?.Id,
                transition.SuccessorAvailableAt),
            cancellationToken);

        await pollStateStore.PutAsync(
            transition.State,
            cancellationToken);

        WarApiTelemetry.Reconciliations.Add(
            1,
            SourceTags(
                context,
                transition.SuccessorAvailableAt is null
                    ? "no_successor"
                    : "successor_scheduled"));
    }

    private async Task<WarApiParseResult?> ParseAndRecordAsync(
        WarApiRegistryContext context,
        FetchDescriptor fetch,
        PayloadDescriptor payload,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();

        WarApiParseResult? parsed = null;
        string outcome;
        string? fingerprint = null;
        var unknownProperties = 0;
        var unknownCodes = 0;
        string? errorCode = null;

        try
        {
            var decoded = await WarApiContentPolicy.DecodeAsync(
                payload.Body,
                fetch.ContentEncoding,
                options.MaxDecodedBytes,
                options.MaxExpansionRatio,
                cancellationToken);

            parsed = _parser.Parse(
                context.SourceEndpoint.Capability,
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

        var completedAt = timeProvider.GetUtcNow();

        await parseRunStore.RecordAsync(
            new SourceParseRunWrite(
                fetch.Id,
                context.Endpoint.CapabilityKey,
                WarApiVersions.Adapter,
                WarApiVersions.Parser,
                JsonStructuralFingerprinter.Algorithm,
                fingerprint,
                outcome,
                unknownProperties,
                unknownCodes,
                errorCode,
                startedAt,
                completedAt,
                parsed?.SourceVersion,
                parsed?.SourceLastUpdated),
            cancellationToken);

        WarApiTelemetry.ParseRuns.Add(
            1,
            SourceTags(context, outcome));

        return parsed;
    }

    private async Task ReconcileMapDiscoveryAsync(
        WarApiRegistryContext context,
        IEnumerable<string> sourceMapNames,
        CancellationToken cancellationToken)
    {
        foreach (var sourceMapName in sourceMapNames.Distinct(StringComparer.Ordinal))
        {
            string mapName;
            try
            {
                mapName = WarApiCatalog.ValidateMapName(sourceMapName);
            }
            catch (ArgumentException exception)
            {
                logger.LogWarning(
                    exception,
                    "Ignoring unsafe map identifier from shard {ShardKey}; raw evidence is retained.",
                    context.Shard.Key);
                continue;
            }

            var endpoints = new List<SourceEndpoint>
            {
                WarApiCatalog.WarReport(mapName),
            };

            if (!IsHomeRegion(mapName))
            {
                endpoints.Add(WarApiCatalog.StaticMap(mapName));
                endpoints.Add(WarApiCatalog.DynamicMap(mapName));
            }

            foreach (var sourceEndpoint in endpoints)
            {
                var registration = await registry.RegisterEndpointAsync(
                    context.Shard.Id,
                    sourceEndpoint.Capability.Key,
                    sourceEndpoint.SemanticKey,
                    cancellationToken);

                if (registration.Status is RegistryRegistrationStatus.Conflict)
                {
                    throw new SourceStateIntegrityException(
                        $"Discovered endpoint '{sourceEndpoint.SemanticKey}' conflicts with durable registry state.");
                }

                var discoveryWindow =
                    collectionProfile.DiscoveryWindow(
                        sourceEndpoint.Capability);
                var target =
                    registration.Resource.CreatedAt +
                    WarApiResponsePolicy.Spread(
                        context.Shard.Key,
                        sourceEndpoint.SemanticKey,
                        discoveryWindow);

                var job = await ingestion.EnqueueAsync(
                    registration.Resource.Id,
                    $"discover@1:{sourceEndpoint.SemanticKey}",
                    target,
                    target,
                    cancellationToken: cancellationToken);

                if (job.Status is JobEnqueueStatus.Conflict)
                {
                    throw new SourceStateIntegrityException(
                        $"Discovery job for '{sourceEndpoint.SemanticKey}' conflicts with durable scheduling.");
                }
            }
        }
    }

    private async Task<EndpointActivity> ResolveEndpointActivityAsync(
        WarApiRegistryContext context,
        CancellationToken cancellationToken)
    {
        var mapName = context.SourceEndpoint.SourceIdentifier;
        if (mapName is null)
        {
            return new EndpointActivity(
                Active: true,
                ActiveMapNames: null);
        }

        var mapsEndpoint = await registry.GetEndpointBySemanticKeyAsync(
            context.Shard.Id,
            "maps",
            cancellationToken);

        if (mapsEndpoint is null)
        {
            return new EndpointActivity(
                Active: true,
                ActiveMapNames: null);
        }

        var mapsSnapshot = await evidenceReader.GetCurrentAsync(
            mapsEndpoint.Id,
            cancellationToken);

        if (mapsSnapshot?.RepresentationFetch is null ||
            mapsSnapshot.RepresentationPayload is null ||
            mapsSnapshot.CurrentFetch.StatusCode is not (
                (int)HttpStatusCode.OK or
                (int)HttpStatusCode.NotModified))
        {
            return new EndpointActivity(
                Active: true,
                ActiveMapNames: null);
        }

        try
        {
            var decoded = await WarApiContentPolicy.DecodeAsync(
                mapsSnapshot.RepresentationPayload.Body,
                mapsSnapshot.RepresentationFetch.ContentEncoding,
                options.MaxDecodedBytes,
                options.MaxExpansionRatio,
                cancellationToken);

            var parsed = _parser.Parse(
                WarApiCapabilities.ActiveMapList,
                decoded);

            if (!parsed.Parsed ||
                parsed.Value is not string[] maps)
            {
                return new EndpointActivity(
                    Active: true,
                    ActiveMapNames: null);
            }

            return new EndpointActivity(
                maps.Contains(
                    mapName,
                    StringComparer.Ordinal),
                maps);
        }
        catch (WarApiDecodingException)
        {
            return new EndpointActivity(
                Active: true,
                ActiveMapNames: null);
        }
    }

    private PollTransition BuildPollTransition(
        WarApiRegistryContext context,
        EndpointEvidenceSnapshot snapshot,
        EndpointPollStateDescriptor? previous,
        bool active,
        TimeSpan cadence,
        string schedulingPolicy)
    {
        var fetch = snapshot.CurrentFetch;

        var previousValidator =
            WarApiRequestBuilder.UsableValidator(previous?.ValidatorEtag);
        var representationFetchId = previous?.RepresentationFetchId;
        var validatorEtag = previousValidator;
        var latestValidationFetchId = previous?.LatestValidationFetchId;
        var lastSuccessAt = previous?.LastSuccessAt;
        var consecutiveFailures = previous?.ConsecutiveFailures ?? 0;
        DateTimeOffset? sourceCacheEligibleAt = fetch.RetrievedAt;
        DateTimeOffset? nextTargetAt = null;
        DateTimeOffset? retryEligibleAt = null;
        DateTimeOffset? successorAvailableAt = null;

        if (fetch.StatusCode == (int)HttpStatusCode.OK &&
            fetch.PayloadId is not null)
        {
            var decision = _cachePolicy.Evaluate(
                ToCacheMetadata(fetch),
                fetch.RetrievedAt,
                cadence);

            consecutiveFailures = 0;
            latestValidationFetchId = fetch.Id;
            lastSuccessAt = fetch.RetrievedAt;
            sourceCacheEligibleAt = decision.SourceCacheEligibleAt;
            nextTargetAt = fetch.RetrievedAt + cadence;

            if (decision.ReusableRepresentation)
            {
                representationFetchId = fetch.Id;
                validatorEtag =
                    WarApiRequestBuilder.UsableValidator(fetch.SourceEtag);
            }
            else
            {
                representationFetchId = null;
                validatorEtag = null;
            }

            if (active)
            {
                successorAvailableAt = decision.NextEligibleAt;
            }
        }
        else if (fetch.StatusCode == (int)HttpStatusCode.NotModified &&
                 snapshot.RepresentationFetch is not null)
        {
            var decision = _cachePolicy.Evaluate(
                ToCacheMetadata(fetch),
                fetch.RetrievedAt,
                cadence);

            consecutiveFailures = 0;
            representationFetchId = snapshot.RepresentationFetch.Id;
            validatorEtag =
                WarApiRequestBuilder.UsableValidator(fetch.SourceEtag) ??
                previousValidator;
            latestValidationFetchId = fetch.Id;
            lastSuccessAt = fetch.RetrievedAt;
            sourceCacheEligibleAt = decision.SourceCacheEligibleAt;
            nextTargetAt = fetch.RetrievedAt + cadence;

            if (active)
            {
                successorAvailableAt = decision.NextEligibleAt;
            }
        }
        else
        {
            consecutiveFailures = checked(consecutiveFailures + 1);
            var backoff = FailureBackoff(
                context,
                fetch,
                consecutiveFailures);

            sourceCacheEligibleAt = CacheEligibilityForFailure(fetch);
            retryEligibleAt = backoff;
            nextTargetAt = fetch.RetrievedAt;

            if (active)
            {
                successorAvailableAt = Max(
                    sourceCacheEligibleAt.Value,
                    retryEligibleAt.Value);
            }

            if (fetch.StatusCode == (int)HttpStatusCode.NotModified)
            {
                representationFetchId = null;
                validatorEtag = null;
            }
        }

        return new PollTransition(
            new EndpointPollStateWrite(
                context.Endpoint.Id,
                fetch.Id,
                latestValidationFetchId,
                representationFetchId,
                validatorEtag,
                sourceCacheEligibleAt,
                active ? nextTargetAt : null,
                retryEligibleAt,
                fetch.StatusCode is null ? previous?.LastHttpResponseAt : fetch.RetrievedAt,
                lastSuccessAt,
                consecutiveFailures,
                schedulingPolicy),
            successorAvailableAt);
    }

    private DateTimeOffset FailureBackoff(
        WarApiRegistryContext context,
        FetchDescriptor fetch,
        int consecutiveFailures)
    {
        var stableKey = $"{context.Shard.Key}/{context.Endpoint.SemanticKey}";
        var localBackoff =
            fetch.StatusCode is { } statusCode &&
            WarApiResponsePolicy.Classify(
                (HttpStatusCode)statusCode,
                context.SourceEndpoint.SourceIdentifier is not null) is
                    WarApiResponseClass.AuthorizationFailure or
                    WarApiResponseClass.ClientFailure or
                    WarApiResponseClass.RootContractFailure or
                    WarApiResponseClass.RedirectFailure
                ? TimeSpan.FromMinutes(5)
                : WarApiResponsePolicy.Backoff(
                    consecutiveFailures,
                    stableKey);

        var localRetryAt = fetch.RetrievedAt + localBackoff;
        var cacheDecision = _cachePolicy.Evaluate(
            ToCacheMetadata(fetch),
            fetch.RetrievedAt,
            TimeSpan.Zero);

        return cacheDecision.RetryEligibleAt is { } sourceRetryAt &&
               sourceRetryAt > localRetryAt
            ? sourceRetryAt
            : localRetryAt;
    }

    private DateTimeOffset CacheEligibilityForFailure(FetchDescriptor fetch)
    {
        if (fetch.StatusCode is null)
        {
            return fetch.RetrievedAt;
        }

        return _cachePolicy.Evaluate(
            ToCacheMetadata(fetch),
            fetch.RetrievedAt,
            TimeSpan.Zero).SourceCacheEligibleAt;
    }

    private static KeyValuePair<string, object?>[] SourceTags(
        WarApiRegistryContext context,
        string outcome) =>
        [
            new("source", WarApiCatalog.SourceKey),
            new("environment", context.Shard.Environment),
            new("shard", context.Shard.Key),
            new("capability", context.Endpoint.CapabilityKey),
            new("outcome", outcome),
        ];

    private static WarApiCacheMetadata ToCacheMetadata(
        FetchDescriptor fetch) =>
        new(
            fetch.StatusCode is { } statusCode
                ? (HttpStatusCode)statusCode
                : 0,
            fetch.CacheControl,
            fetch.ExpiresAt,
            fetch.SourceDate,
            fetch.SourceAgeSeconds,
            fetch.RetryAfter);

    private static string ToParseOutcome(WarApiParseOutcome outcome) =>
        outcome switch
        {
            WarApiParseOutcome.Parsed => "parsed",
            WarApiParseOutcome.ParsedWithUnknowns => "parsed_with_unknowns",
            WarApiParseOutcome.MalformedJson => "malformed_json",
            WarApiParseOutcome.IncompatibleShape => "incompatible_shape",
            _ => "unknown",
        };

    private static bool IsHomeRegion(string mapName) =>
        mapName is "HomeRegionC" or "HomeRegionW";

    private static DateTimeOffset Max(
        DateTimeOffset first,
        DateTimeOffset second) =>
        first > second ? first : second;

    private sealed record EndpointActivity(
        bool Active,
        IReadOnlyList<string>? ActiveMapNames);

    private sealed record PollTransition(
        EndpointPollStateWrite State,
        DateTimeOffset? SuccessorAvailableAt);
}
