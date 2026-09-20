using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;

namespace FoxData.Application.Evidence;

public sealed record SourceResponseObservation(
    DateTimeOffset RequestStartedAt,
    DateTimeOffset? ResponseStartedAt,
    DateTimeOffset RetrievedAt,
    string TransportKind,
    int? StatusCode,
    string? MediaType,
    string? ContentEncoding,
    long? DeclaredLength,
    string? SourceEtag,
    string? CacheControl,
    DateTimeOffset? ExpiresAt,
    long DurationMs,
    DateTimeOffset? SourceDate = null,
    long? SourceAgeSeconds = null,
    string? RetryAfter = null,
    string? BodyErrorCode = null);

public sealed record PayloadDescriptor(
    PayloadId Id,
    PayloadHash Hash,
    long ByteLength,
    ReadOnlyMemory<byte> Body,
    DateTimeOffset CreatedAt);

public sealed record FetchDescriptor(
    FetchId Id,
    IngestionAttemptId AttemptId,
    EndpointId EndpointId,
    DateTimeOffset RequestStartedAt,
    DateTimeOffset? ResponseStartedAt,
    DateTimeOffset RetrievedAt,
    string TransportKind,
    int? StatusCode,
    string? MediaType,
    string? ContentEncoding,
    long? DeclaredLength,
    string? SourceEtag,
    string? CacheControl,
    DateTimeOffset? ExpiresAt,
    PayloadId? PayloadId,
    FetchId? PriorFetchId,
    long DurationMs,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SourceDate = null,
    long? SourceAgeSeconds = null,
    string? RetryAfter = null,
    string? BodyErrorCode = null);

public enum CaptureStatus
{
    CapturedCurrent,
    CapturedLate,
    AlreadyCaptured,
    InvalidAttempt,
    InvalidState,
    EndpointMismatch,
    LeaseGenerationMismatch,
    FenceMismatch,
    InvalidPriorFetch,
}

public sealed record CaptureResult(
    CaptureStatus Status,
    FetchDescriptor? Fetch,
    PayloadDescriptor? Payload,
    bool PayloadDeduplicated = false)
{
    public bool Captured =>
        Status is CaptureStatus.CapturedCurrent or CaptureStatus.CapturedLate;

    public bool IsCurrentCapture => Status is CaptureStatus.CapturedCurrent;
}

public sealed class EvidenceIntegrityException : Exception
{
    public EvidenceIntegrityException(string message)
        : base(message)
    {
    }
}
