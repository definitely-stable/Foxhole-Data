using FoxData.Core.Evidence;

namespace FoxData.Application.Canonical;

public enum NormalizationRunOutcome
{
    Normalized,
    Rejected,
    Failed,
}

public sealed record NormalizationRunDescriptor(
    NormalizationRunId Id,
    SourceParseRunId SourceParseRunId,
    string NormalizerVersion,
    NormalizationRunOutcome Outcome,
    string? ErrorCode,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    DateTimeOffset CreatedAt);

public sealed record NormalizationRunWrite(
    SourceParseRunId SourceParseRunId,
    string NormalizerVersion,
    NormalizationRunOutcome Outcome,
    string? ErrorCode,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public interface INormalizationRunStore
{
    Task<NormalizationRunDescriptor?> GetAsync(
        SourceParseRunId sourceParseRunId,
        string normalizerVersion,
        CancellationToken cancellationToken);

    Task<NormalizationRunDescriptor> RecordAsync(
        NormalizationRunWrite run,
        CancellationToken cancellationToken);
}

public sealed class CanonicalStateIntegrityException : Exception
{
    public CanonicalStateIntegrityException(string message)
        : base(message)
    {
    }
}
