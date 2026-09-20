using System.Diagnostics;
using FoxData.Application.Diagnostics;
using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;

namespace FoxData.Application.Evidence;

public sealed class EvidenceKernel
{
    private readonly IEvidenceKernelStore _store;
    private readonly EvidenceKernelLimits _limits;

    public EvidenceKernel(IEvidenceKernelStore store)
        : this(store, new EvidenceKernelLimits())
    {
    }

    public EvidenceKernel(
        IEvidenceKernelStore store,
        EvidenceKernelLimits limits)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public async Task<CaptureResult> CaptureSourceResponseAsync(
        IngestionAttemptId attemptId,
        EndpointId endpointId,
        LeaseGeneration expectedLeaseGeneration,
        FenceToken expectedFenceToken,
        SourceResponseObservation observation,
        ReadOnlyMemory<byte>? body,
        FetchId? priorFetchId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(attemptId.Value, nameof(attemptId));
        EnsureNonEmpty(endpointId.Value, nameof(endpointId));

        if (expectedLeaseGeneration.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedLeaseGeneration),
                expectedLeaseGeneration.Value,
                "Lease generation must be positive.");
        }

        if (expectedFenceToken.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedFenceToken),
                expectedFenceToken.Value,
                "Fence token must be positive.");
        }

        var normalizedObservation = NormalizeObservation(observation);

        if (body is not null && priorFetchId is not null)
        {
            throw new ArgumentException(
                "PriorFetchId is only valid for an explicit no-body observation.",
                nameof(priorFetchId));
        }

        if (priorFetchId is { } prior)
        {
            EnsureNonEmpty(prior.Value, nameof(priorFetchId));
        }

        ReadOnlyMemory<byte>? stableBody = null;
        PayloadHash? payloadHash = null;
        PayloadId? proposedPayloadId = null;

        if (body is { } suppliedBody)
        {
            if (suppliedBody.Length > _limits.MaxPayloadBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(body),
                    suppliedBody.Length,
                    $"Payload exceeds the configured Evidence Kernel limit of {_limits.MaxPayloadBytes} bytes.");
            }

            var copy = suppliedBody.ToArray();
            stableBody = copy;
            payloadHash = PayloadHash.Compute(copy);
            proposedPayloadId = PayloadId.New();
        }

        using var activity = KernelTelemetry.ActivitySource.StartActivity("evidence.capture");
        activity?.SetTag("foxdata.attempt.id", attemptId.ToString());
        activity?.SetTag("foxdata.endpoint.id", endpointId.ToString());
        activity?.SetTag("foxdata.lease.generation", expectedLeaseGeneration.Value);
        activity?.SetTag("foxdata.fence.token", expectedFenceToken.Value);

        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var result = await _store.CaptureSourceResponseAsync(
                FetchId.New(),
                proposedPayloadId,
                attemptId,
                endpointId,
                expectedLeaseGeneration,
                expectedFenceToken,
                normalizedObservation,
                payloadHash,
                stableBody,
                priorFetchId,
                cancellationToken);

            var outcome = ToMetricOutcome(result.Status);
            var outcomeTag = new KeyValuePair<string, object?>("outcome", outcome);

            KernelTelemetry.CaptureDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                outcomeTag);

            if (result.Captured)
            {
                KernelTelemetry.FetchesCaptured.Add(1, outcomeTag);

                if (stableBody is { } capturedBody)
                {
                    KernelTelemetry.PayloadBytes.Add(capturedBody.Length, outcomeTag);
                }

                if (result.PayloadDeduplicated)
                {
                    KernelTelemetry.PayloadDedupeHits.Add(1);
                }
            }

            activity?.SetTag("foxdata.capture.outcome", outcome);

            return result;
        }
        catch
        {
            KernelTelemetry.CaptureDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("outcome", "error"));
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    public Task<FetchDescriptor?> GetAttemptEvidenceAsync(
        IngestionAttemptId attemptId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(attemptId.Value, nameof(attemptId));
        return _store.GetFetchByAttemptAsync(attemptId, cancellationToken);
    }

    public Task<FetchDescriptor?> GetFetchAsync(
        FetchId fetchId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(fetchId.Value, nameof(fetchId));
        return _store.GetFetchAsync(fetchId, cancellationToken);
    }

    public Task<PayloadDescriptor?> GetPayloadAsync(
        PayloadId payloadId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(payloadId.Value, nameof(payloadId));
        return _store.GetPayloadAsync(payloadId, cancellationToken);
    }

    private static SourceResponseObservation NormalizeObservation(SourceResponseObservation value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (string.IsNullOrWhiteSpace(value.TransportKind) ||
            value.TransportKind.Length > 64 ||
            !string.Equals(value.TransportKind, value.TransportKind.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "TransportKind must be non-empty, already trimmed, and at most 64 characters.",
                nameof(value));
        }

        if (value.StatusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value.StatusCode,
                "StatusCode must be a valid HTTP-style status code when supplied.");
        }

        if (value.DeclaredLength is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value.DeclaredLength,
                "DeclaredLength must not be negative.");
        }

        if (value.DurationMs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value.DurationMs,
                "DurationMs must not be negative.");
        }

        ValidateOptionalLength(value.MediaType, 256, nameof(value.MediaType));
        ValidateOptionalLength(value.ContentEncoding, 128, nameof(value.ContentEncoding));
        ValidateOptionalLength(value.SourceEtag, 1024, nameof(value.SourceEtag));
        ValidateOptionalLength(value.CacheControl, 2048, nameof(value.CacheControl));
        ValidateOptionalLength(value.RetryAfter, 1024, nameof(value.RetryAfter));

        if (value.SourceAgeSeconds is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value.SourceAgeSeconds,
                "SourceAgeSeconds must not be negative.");
        }

        var requestStartedAt = NormalizeTimestamp(value.RequestStartedAt);
        DateTimeOffset? responseStartedAt = value.ResponseStartedAt is null
            ? null
            : NormalizeTimestamp(value.ResponseStartedAt.Value);
        var retrievedAt = NormalizeTimestamp(value.RetrievedAt);
        DateTimeOffset? expiresAt = value.ExpiresAt is null
            ? null
            : NormalizeTimestamp(value.ExpiresAt.Value);
        DateTimeOffset? sourceDate = value.SourceDate is null
            ? null
            : NormalizeTimestamp(value.SourceDate.Value);

        if (responseStartedAt < requestStartedAt)
        {
            throw new ArgumentException(
                "ResponseStartedAt must not be earlier than RequestStartedAt.",
                nameof(value));
        }

        if (retrievedAt < requestStartedAt ||
            responseStartedAt is not null && retrievedAt < responseStartedAt)
        {
            throw new ArgumentException(
                "RetrievedAt must not be earlier than request/response observation time.",
                nameof(value));
        }

        return value with
        {
            RequestStartedAt = requestStartedAt,
            ResponseStartedAt = responseStartedAt,
            RetrievedAt = retrievedAt,
            ExpiresAt = expiresAt,
            SourceDate = sourceDate,
        };
    }

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset value)
    {
        const long ticksPerMicrosecond = 10;
        var utc = value.ToUniversalTime();
        var normalizedTicks = utc.Ticks - (utc.Ticks % ticksPerMicrosecond);

        return new DateTimeOffset(normalizedTicks, TimeSpan.Zero);
    }

    private static string ToMetricOutcome(CaptureStatus status) =>
        status switch
        {
            CaptureStatus.CapturedCurrent => "current",
            CaptureStatus.CapturedLate => "late",
            CaptureStatus.AlreadyCaptured => "already_captured",
            CaptureStatus.InvalidAttempt => "invalid_attempt",
            CaptureStatus.InvalidState => "invalid_state",
            CaptureStatus.EndpointMismatch => "endpoint_mismatch",
            CaptureStatus.LeaseGenerationMismatch => "lease_generation_mismatch",
            CaptureStatus.FenceMismatch => "fence_mismatch",
            CaptureStatus.InvalidPriorFetch => "invalid_prior_fetch",
            _ => "unknown",
        };

    private static void ValidateOptionalLength(string? value, int maximum, string parameterName)
    {
        if (value is not null && value.Length > maximum)
        {
            throw new ArgumentException(
                $"{parameterName} must be at most {maximum} characters.",
                parameterName);
        }
    }

    private static void EnsureNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier must not be empty.", parameterName);
        }
    }
}
