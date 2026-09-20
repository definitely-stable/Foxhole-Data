using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
using FoxData.Application.Sources;
using FoxData.Core.Ingestion;
using FoxData.Sources.WarApi;
using Microsoft.Extensions.DependencyInjection;

namespace FoxData.Worker;

public sealed class WarApiExecutorWorker(
    IServiceScopeFactory scopeFactory,
    WarApiWorkerOptions options,
    IWarApiTransport transport,
    TimeProvider timeProvider,
    ILogger<WarApiExecutorWorker> logger) : BackgroundService
{
    private readonly WorkerInstanceId _workerId = WorkerInstanceId.New();

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
                var ingestion = scope.ServiceProvider.GetRequiredService<IngestionKernel>();

                var claim = await ingestion.ClaimNextForSourceAsync(
                    _workerId,
                    WarApiCatalog.SourceKey,
                    options.LeaseDuration,
                    stoppingToken);

                if (!claim.Claimed)
                {
                    await Task.Delay(options.IdleDelay, stoppingToken);
                    continue;
                }

                await ExecuteClaimAsync(
                    scope.ServiceProvider,
                    claim.Job!,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "War API executor iteration failed.");
                await DelayAfterFailureAsync(stoppingToken);
            }
        }
    }

    private async Task ExecuteClaimAsync(
        IServiceProvider services,
        CollectionJobDescriptor job,
        CancellationToken stoppingToken)
    {
        var ingestion = services.GetRequiredService<IngestionKernel>();
        var evidence = services.GetRequiredService<EvidenceKernel>();
        var pollState = services.GetRequiredService<IEndpointPollStateStore>();
        var resolver = services.GetRequiredService<WarApiRegistryResolver>();

        var attemptId = IngestionAttemptId.New();
        var begun = await ingestion.BeginAttemptAsync(
            attemptId,
            job.Id,
            _workerId,
            job.LeaseGeneration,
            stoppingToken);

        if (begun.Status is not BeginAttemptStatus.Started)
        {
            logger.LogWarning(
                "Could not begin War API attempt for job {JobId}: {Status}.",
                job.Id,
                begun.Status);
            return;
        }

        var fenced = await ingestion.AcquireEndpointFenceAsync(
            attemptId,
            _workerId,
            job.LeaseGeneration,
            stoppingToken);

        if (fenced.Status is not FenceAcquireStatus.AcquiredNow ||
            fenced.Attempt?.FenceToken is not { } fenceToken)
        {
            logger.LogWarning(
                "Could not acquire endpoint fence for attempt {AttemptId}: {Status}.",
                attemptId,
                fenced.Status);
            return;
        }

        WarApiRegistryContext context;
        HttpRequestMessage request;
        EndpointPollStateDescriptor? currentPollState;

        try
        {
            context = await resolver.ResolveAsync(job.EndpointId, stoppingToken);
            currentPollState = await pollState.GetAsync(
                job.EndpointId,
                stoppingToken);

            var validator = currentPollState?.RepresentationFetchId is not null
                ? currentPollState.ValidatorEtag
                : null;

            request = new WarApiRequestBuilder().Build(
                context.WarApiShard,
                context.SourceEndpoint,
                validator);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "War API request construction failed for endpoint {EndpointId}.",
                job.EndpointId);

            await ingestion.DeferBeforeExchangeAsync(
                attemptId,
                _workerId,
                job.LeaseGeneration,
                timeProvider.GetUtcNow().AddMinutes(5),
                "war_api_request",
                "request_construction_failed",
                stoppingToken);
            return;
        }

        using (request)
        {
            var authorization = await ingestion.AuthorizeExchangeAsync(
                attemptId,
                _workerId,
                job.LeaseGeneration,
                stoppingToken);

            if (!authorization.MayPerformExchange)
            {
                logger.LogWarning(
                    "War API exchange was not newly authorized for attempt {AttemptId}: {Status}.",
                    attemptId,
                    authorization.Status);
                return;
            }

            WarApiHttpExchangeResult response;
            try
            {
                response = await transport.SendAsync(
                    request,
                    stoppingToken);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or
                IOException or
                WarApiResponseLimitException or
                OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Authorized War API exchange ended without durable response evidence for attempt {AttemptId}.",
                    attemptId);

                await DeferUncertainBestEffortAsync(
                    ingestion,
                    attemptId,
                    job,
                    begun.Attempt!.AttemptNumber,
                    "war_api_transport",
                    exception is WarApiResponseLimitException
                        ? "response_limit"
                        : "exchange_uncertain");
                return;
            }

            var observation = new SourceResponseObservation(
                response.RequestStartedAt,
                response.ResponseStartedAt,
                response.RetrievedAt,
                "war-api",
                (int)response.StatusCode,
                response.MediaType,
                response.ContentEncoding,
                response.DeclaredLength,
                response.Etag,
                response.CacheControl,
                response.ExpiresAt,
                response.DurationMs,
                response.SourceDate,
                response.Age is { } age
                    ? checked((long)age.TotalSeconds)
                    : null,
                response.RetryAfter);

            var priorFetchId =
                response.StatusCode == System.Net.HttpStatusCode.NotModified
                    ? currentPollState?.RepresentationFetchId
                    : null;

            await CaptureWithReconciliationAsync(
                evidence,
                ingestion,
                attemptId,
                job,
                fenceToken,
                begun.Attempt!.AttemptNumber,
                observation,
                response.Body,
                priorFetchId);
        }
    }

    private async Task CaptureWithReconciliationAsync(
        EvidenceKernel evidence,
        IngestionKernel ingestion,
        IngestionAttemptId attemptId,
        CollectionJobDescriptor job,
        FenceToken fenceToken,
        int attemptNumber,
        SourceResponseObservation observation,
        byte[]? body,
        FoxData.Core.Evidence.FetchId? priorFetchId)
    {
        try
        {
            var captured = await evidence.CaptureSourceResponseAsync(
                attemptId,
                job.EndpointId,
                job.LeaseGeneration,
                fenceToken,
                observation,
                body,
                priorFetchId,
                CancellationToken.None);

            if (!captured.Captured &&
                captured.Status is not CaptureStatus.AlreadyCaptured)
            {
                logger.LogWarning(
                    "War API response capture for attempt {AttemptId} returned {Status}.",
                    attemptId,
                    captured.Status);
            }

            return;
        }
        catch (Exception firstException)
        {
            logger.LogWarning(
                firstException,
                "War API raw capture outcome is uncertain for attempt {AttemptId}; reconciling by AttemptId.",
                attemptId);
        }

        using var reconciliationDeadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(5),
            timeProvider);

        try
        {
            var existing = await evidence.GetAttemptEvidenceAsync(
                attemptId,
                reconciliationDeadline.Token);

            if (existing is not null)
            {
                logger.LogInformation(
                    "War API capture for attempt {AttemptId} committed before the database outcome became uncertain.",
                    attemptId);
                return;
            }

            var retriedCapture = await evidence.CaptureSourceResponseAsync(
                attemptId,
                job.EndpointId,
                job.LeaseGeneration,
                fenceToken,
                observation,
                body,
                priorFetchId,
                reconciliationDeadline.Token);

            if (retriedCapture.Captured ||
                retriedCapture.Status is CaptureStatus.AlreadyCaptured)
            {
                return;
            }

            logger.LogWarning(
                "Reconciled War API capture for attempt {AttemptId} returned {Status}.",
                attemptId,
                retriedCapture.Status);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "War API capture reconciliation failed for attempt {AttemptId}.",
                attemptId);
        }

        await DeferUncertainBestEffortAsync(
            ingestion,
            attemptId,
            job,
            attemptNumber,
            "evidence_capture",
            "capture_uncertain");
    }

    private async Task DeferUncertainBestEffortAsync(
        IngestionKernel ingestion,
        IngestionAttemptId attemptId,
        CollectionJobDescriptor job,
        int attemptNumber,
        string errorClass,
        string errorCode)
    {
        var retryAt = timeProvider.GetUtcNow() +
            WarApiResponsePolicy.Backoff(
                Math.Max(1, attemptNumber),
                job.EndpointId.ToString());

        using var cleanupDeadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(5),
            timeProvider);

        try
        {
            await ingestion.DeferUncertainExchangeAsync(
                attemptId,
                _workerId,
                job.LeaseGeneration,
                retryAt,
                errorClass,
                errorCode,
                cleanupDeadline.Token);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Could not persist uncertain state for attempt {AttemptId}; M2 lease expiry recovery remains authoritative.",
                attemptId);
        }
    }

    private async Task DelayAfterFailureAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(options.IdleDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
