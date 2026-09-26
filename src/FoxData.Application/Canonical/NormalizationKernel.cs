using FoxData.Core.Evidence;

namespace FoxData.Application.Canonical;

public sealed class NormalizationKernel(INormalizationRunStore store)
{
    public Task<NormalizationRunDescriptor?> GetAsync(
        SourceParseRunId sourceParseRunId,
        string normalizerVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateId(sourceParseRunId);
        return store.GetAsync(
            sourceParseRunId,
            ValidateRequiredText(normalizerVersion, 128, nameof(normalizerVersion)),
            cancellationToken);
    }

    public Task<NormalizationRunDescriptor> RecordAsync(
        NormalizationRunWrite run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        ValidateId(run.SourceParseRunId);
        _ = ValidateRequiredText(
            run.NormalizerVersion,
            128,
            nameof(run.NormalizerVersion));

        if (run.ErrorCode is { Length: > 128 })
        {
            throw new ArgumentException(
                "ErrorCode must be at most 128 characters.",
                nameof(run));
        }

        if (run.ErrorCode is not null &&
            !string.Equals(run.ErrorCode, run.ErrorCode.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "ErrorCode must already be trimmed.",
                nameof(run));
        }

        if (run.CompletedAt < run.StartedAt)
        {
            throw new ArgumentException(
                "CompletedAt must not be earlier than StartedAt.",
                nameof(run));
        }

        return store.RecordAsync(run, cancellationToken);
    }

    private static void ValidateId(SourceParseRunId id)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Source parse run identifier must not be empty.",
                nameof(id));
        }
    }

    private static string ValidateRequiredText(
        string value,
        int maximum,
        string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);

        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximum ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{name} must be non-empty, already trimmed, and at most {maximum} characters.",
                name);
        }

        return value;
    }
}
