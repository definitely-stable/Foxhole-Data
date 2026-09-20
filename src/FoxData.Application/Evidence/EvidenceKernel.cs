using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;

namespace FoxData.Application.Evidence;

public sealed class EvidenceKernel(IEvidenceKernelStore store)
{
    public Task<CaptureResult> CaptureSourceResponseAsync(
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
            var copy = suppliedBody.ToArray();
            stableBody = copy;
            payloadHash = PayloadHash.Compute(copy);
            proposedPayloadId = PayloadId.New();
        }

        return store.CaptureSourceResponseAsync(
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
    }

    public Task<FetchDescriptor?> GetAttemptEvidenceAsync(
        IngestionAttemptId attemptId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(attemptId.Value, nameof(attemptId));
        return store.GetFetchByAttemptAsync(attemptId, cancellationToken);
    }

    public Task<FetchDescriptor?> GetFetchAsync(
        FetchId fetchId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(fetchId.Value, nameof(fetchId));
        return store.GetFetchAsync(fetchId, cancellationToken);
    }

    public Task<PayloadDescriptor?> GetPayloadAsync(
        PayloadId payloadId,
        CancellationToken cancellationToken = default)
    {
        EnsureNonEmpty(payloadId.Value, nameof(payloadId));
        return store.GetPayloadAsync(payloadId, cancellationToken);
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

        var requestStartedAt = NormalizeTimestamp(value.RequestStartedAt);
        DateTimeOffset? responseStartedAt = value.ResponseStartedAt is null
            ? null
            : NormalizeTimestamp(value.ResponseStartedAt.Value);
        var retrievedAt = NormalizeTimestamp(value.RetrievedAt);
        DateTimeOffset? expiresAt = value.ExpiresAt is null
            ? null
            : NormalizeTimestamp(value.ExpiresAt.Value);

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
        };
    }

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset value)
    {
        const long ticksPerMicrosecond = 10;
        var utc = value.ToUniversalTime();
        var normalizedTicks = utc.Ticks - (utc.Ticks % ticksPerMicrosecond);

        return new DateTimeOffset(normalizedTicks, TimeSpan.Zero);
    }

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
