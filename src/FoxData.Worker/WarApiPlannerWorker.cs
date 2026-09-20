using System.Net;
using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
using FoxData.Application.Sources;
using FoxData.Core.Evidence;
using FoxData.Core.Sources;
using FoxData.Sources.Abstractions;
using FoxData.Sources.WarApi;
using Microsoft.Extensions.DependencyInjection;

namespace FoxData.Worker;

public sealed class WarApiPlannerWorker(
    IServiceScopeFactory scopeFactory,
    WarApiWorkerOptions options,
    TimeProvider timeProvider,
    ILogger<WarApiPlannerWorker> logger) : BackgroundService
{
    private readonly WarApiParser _parser = new();
    private readonly WarApiCachePolicy _cachePolicy = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var planningReader =
                    scope.ServiceProvider.GetRequiredService<ISourcePlanningReader>();

                var endpointIds =
                    await planningReader.ListPendingCurrentEndpointsAsync(
                        WarApiCatalog.SourceKey,
                        options.PlannerBatchSize,
                        stoppingToken);

                if (endpointIds.Count == 0)
                {
                    await Task.Delay(options.PlannerInterval, stoppingToken);
                    continue;
                }

                foreach (var endpointId in endpointIds)
                {
                    try
                    {
                        await ProcessEndpointAsync(
                            scope.ServiceProvider,
                            endpointId,
                            stoppingToken);
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(
                            exception,
                            "War API reconciliation failed for endpoint {EndpointId}.",
                            endpointId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "War API planner iteration failed.");
                await DelayAfterFailureAsync(stoppingToken);
            }
        }
    }

    private async Task ProcessEndpointAsync(
        IServiceProvider services,
        EndpointId endpointId,
        CancellationToken cancellationToken)
    {
        var resolver = services.GetRequiredService<WarApiRegistryResolver>();
        var evidenceReader =
            services.GetRequiredService<IEndpointEvidenceReader>();
        var pollStateStore =
            services.GetRequiredService<IEndpointPollStateStore>();
        var parseRunStore =
            services.GetRequiredService<ISourceParseRunStore>();
        var registry = services.GetRequiredService<SourceRegistry>();
        var ingestion = services.GetRequiredService<IngestionKernel>();

        var context = await resolver.ResolveAsync(
            endpointId,
            cancellationToken);
        var snapshot = await evidenceReader.GetCurrentAsync(
            endpointId,
            cancellationToken);

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
                parseRunStore,
                cancellationToken);

            if (context.SourceEndpoint.Capability ==
                    WarApiCapabilities.ActiveMapList &&
                parsed?.Parsed is true &&
                parsed.Value is string[] maps)
            {
                await ReconcileMapDiscoveryAsync(
                    context,
                    snapshot.CurrentFetch,
                    maps,
                    registry,
                    ingestion,
                    cancellationToken);
            }
        }

        var active = await IsEndpointActiveAsync(
            context,
            registry,
            evidenceReader,
            cancellationToken);

        var transition = BuildPollTransition(
            context,
            snapshot,
            previousPoll,
            active);

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
        }

        await pollStateStore.PutAsync(
            transition.State,
            cancellationToken);
    }

    private async Task<WarApiParseResult?> ParseAndRecordAsync(
        WarApiRegistryContext context,
        FetchDescriptor fetch,
        PayloadDescriptor payload,
        ISourceParseRunStore parseRunStore,
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
                completedAt),
            cancellationToken);

        return parsed;
    }

    private async Task ReconcileMapDiscoveryAsync(
        WarApiRegistryContext context,
        FetchDescriptor mapsFetch,
        IEnumerable<string> sourceMapNames,
        SourceRegistry registry,
        IngestionKernel ingestion,
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

                var cadence = WarApiResponsePolicy.Cadence(
                    sourceEndpoint.Capability);
                var target =
                    mapsFetch.RetrievedAt +
                    WarApiResponsePolicy.Spread(
                        context.Shard.Key,
                        sourceEndpoint.SemanticKey,
                        cadence);

                var job = await ingestion.EnqueueAsync(
                    registration.Resource.Id,
                    $"discover:{mapsFetch.Id}:{sourceEndpoint.SemanticKey}",
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

    private async Task<bool> IsEndpointActiveAsync(
        WarApiRegistryContext context,
        SourceRegistry registry,
        IEndpointEvidenceReader evidenceReader,
        CancellationToken cancellationToken)
    {
        var mapName = context.SourceEndpoint.SourceIdentifier;
        if (mapName is null)
        {
            return true;
        }

        var mapsEndpoint = await registry.GetEndpointBySemanticKeyAsync(
            context.Shard.Id,
            "maps",
            cancellationToken);

        if (mapsEndpoint is null)
        {
            return true;
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
            return true;
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

            return parsed.Parsed &&
                parsed.Value is string[] maps &&
                maps.Contains(mapName, StringComparer.Ordinal);
        }
        catch (WarApiDecodingException)
        {
            return true;
        }
    }

    private PollTransition BuildPollTransition(
        WarApiRegistryContext context,
        EndpointEvidenceSnapshot snapshot,
        EndpointPollStateDescriptor? previous,
        bool active)
    {
        var fetch = snapshot.CurrentFetch;
        var cadence = WarApiResponsePolicy.Cadence(
            context.SourceEndpoint.Capability);

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
                WarApiVersions.PollPolicy),
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

    private async Task DelayAfterFailureAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(options.PlannerInterval, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private sealed record PollTransition(
        EndpointPollStateWrite State,
        DateTimeOffset? SuccessorAvailableAt);
}
