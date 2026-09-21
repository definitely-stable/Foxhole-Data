using System.Diagnostics;
using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
using FoxData.Application.Sources;
using FoxData.Core.Ingestion;
using FoxData.Sources.WarApi;

namespace FoxData.Worker;

public sealed class WarApiAttemptExecutor(
    IngestionKernel ingestion,
    EvidenceKernel evidence,
    IEndpointPollStateStore pollState,
    WarApiRegistryResolver resolver,
    WarApiOutboundRateGovernor rateGovernor,
    IWarApiTransport transport,
    TimeProvider timeProvider,
    ILogger<WarApiAttemptExecutor> logger)
{
    public async Task ExecuteAsync(
        CollectionJobDescriptor job,
        WorkerInstanceId workerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var attemptId = IngestionAttemptId.New();
        var begun = await ingestion.BeginAttemptAsync(
            attemptId,
            job.Id,
            workerId,
            job.LeaseGeneration,
            cancellationToken);

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
            workerId,
            job.LeaseGeneration,
            cancellationToken);

        if (fenced.Status is not FenceAcquireStatus.AcquiredNow ||
            fenced.Attempt?.FenceToken is not { } fenceToken)
        {
            logger.LogWarning(
                "Could not acquire endpoint fence for attempt {AttemptId}: {Status}.",
                attemptId,
                fenced.Status);
            return;
        }

        HttpRequestMessage request;
        EndpointPollStateDescriptor? currentPollState;
        WarApiRegistryContext context;

        try
        {
            context = await resolver.ResolveAsync(
                job.EndpointId,
                cancellationToken);
            currentPollState = await pollState.GetAsync(
                job.EndpointId,
                cancellationToken);

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

            await DeferBeforeExchangeBestEffortAsync(
                workerId,
                attemptId,
                job,
                "war_api_request",
                cancellationToken.IsCancellationRequested
                    ? "request_cancelled"
                    : "request_construction_failed");
            return;
        }

        using (request)
        {
            try
            {
                await rateGovernor.WaitAsync(
                    request.RequestUri
                        ?? throw new InvalidOperationException(
                            "War API request URI must be set before traffic admission."),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await DeferBeforeExchangeBestEffortAsync(
                    workerId,
                    attemptId,
                    job,
                    "war_api_traffic_gate",
                    "request_cancelled");
                return;
            }

            var authorization = await ingestion.AuthorizeExchangeAsync(
                attemptId,
                workerId,
                job.LeaseGeneration,
                cancellationToken);

            if (!authorization.MayPerformExchange)
            {
                logger.LogWarning(
                    "War API exchange was not newly authorized for attempt {AttemptId}: {Status}.",
                    attemptId,
                    authorization.Status);
                return;
            }

            using var activity = WarApiTelemetry.ActivitySource.StartActivity(
                "warapi.exchange",
                ActivityKind.Client);
            activity?.SetTag("foxdata.source", WarApiCatalog.SourceKey);
            activity?.SetTag("foxdata.environment", context.Shard.Environment);
            activity?.SetTag("foxdata.shard", context.Shard.Key);
            activity?.SetTag("foxdata.capability", context.Endpoint.CapabilityKey);
            activity?.SetTag("foxdata.endpoint.semantic_key", context.Endpoint.SemanticKey);
            activity?.SetTag("foxdata.attempt.id", attemptId.ToString());

            WarApiTelemetry.Requests.Add(
                1,
                SourceTags(context));

            WarApiHttpExchangeResult response;
            try
            {
                response = await transport.SendAsync(
                    request,
                    cancellationToken);
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

                WarApiTelemetry.UncertainExchanges.Add(
                    1,
                    SourceTags(context, "transport_uncertain"));
                activity?.SetStatus(ActivityStatusCode.Error);
                activity?.SetTag("foxdata.exchange.outcome", "uncertain");

                await DeferUncertainBestEffortAsync(
                    workerId,
                    attemptId,
                    job,
                    begun.Attempt!.AttemptNumber,
                    "war_api_transport",
                    exception is WarApiResponseLimitException
                        ? "response_limit"
                        : "exchange_uncertain");
                return;
            }

            var responseOutcome = response.BodyErrorCode is null
                ? ResponseOutcome(response.StatusCode)
                : "body_error";
            WarApiTelemetry.Responses.Add(
                1,
                SourceTags(context, responseOutcome));
            WarApiTelemetry.RequestDuration.Record(
                response.DurationMs,
                SourceTags(context, responseOutcome));

            if (response.Body is { } responseBody)
            {
                WarApiTelemetry.ResponseBytes.Add(
                    responseBody.LongLength,
                    SourceTags(context, responseOutcome));
            }

            activity?.SetTag("http.response.status_code", (int)response.StatusCode);
            activity?.SetTag("foxdata.exchange.outcome", responseOutcome);

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
                response.RetryAfter,
                response.BodyErrorCode);

            var priorFetchId =
                response.StatusCode == System.Net.HttpStatusCode.NotModified
                    ? currentPollState?.RepresentationFetchId
                    : null;

            await CaptureWithReconciliationAsync(
                workerId,
                attemptId,
                job,
                fenceToken,
                begun.Attempt!.AttemptNumber,
                observation,
                response.Body is null
                    ? (ReadOnlyMemory<byte>?)null
                    : response.Body,
                priorFetchId);
        }
    }

    private static KeyValuePair<string, object?>[] SourceTags(
        WarApiRegistryContext context,
        string? outcome = null)
    {
        var tags = new List<KeyValuePair<string, object?>>(5)
        {
            new("source", WarApiCatalog.SourceKey),
            new("environment", context.Shard.Environment),
            new("shard", context.Shard.Key),
            new("capability", context.Endpoint.CapabilityKey),
        };

        if (outcome is not null)
        {
            tags.Add(new("outcome", outcome));
        }

        return tags.ToArray();
    }

    private static string ResponseOutcome(System.Net.HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code switch
        {
            200 => "200",
            304 => "304",
            >= 300 and < 400 => "3xx",
            >= 400 and < 500 => "4xx",
            >= 500 and < 600 => "5xx",
            _ => "other",
        };
    }

    private async Task CaptureWithReconciliationAsync(
        WorkerInstanceId workerId,
        IngestionAttemptId attemptId,
        CollectionJobDescriptor job,
        FenceToken fenceToken,
        int attemptNumber,
        SourceResponseObservation observation,
        ReadOnlyMemory<byte>? body,
        FoxData.Core.Evidence.FetchId? priorFetchId)
    {
        using var captureDeadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(10),
            timeProvider);

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
                captureDeadline.Token);

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
            workerId,
            attemptId,
            job,
            attemptNumber,
            "evidence_capture",
            "capture_uncertain");
    }

    private async Task DeferBeforeExchangeBestEffortAsync(
        WorkerInstanceId workerId,
        IngestionAttemptId attemptId,
        CollectionJobDescriptor job,
        string errorClass,
        string errorCode)
    {
        using var cleanupDeadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(5),
            timeProvider);

        try
        {
            await ingestion.DeferBeforeExchangeAsync(
                attemptId,
                workerId,
                job.LeaseGeneration,
                timeProvider.GetUtcNow().AddMinutes(5),
                errorClass,
                errorCode,
                cleanupDeadline.Token);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Could not persist pre-exchange deferral for attempt {AttemptId}; M2 lease expiry recovery remains authoritative.",
                attemptId);
        }
    }

    private async Task DeferUncertainBestEffortAsync(
        WorkerInstanceId workerId,
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
                workerId,
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
}
