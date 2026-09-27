using FoxData.Core.Evidence;
using FoxData.Core.Runtime;
using FoxData.Core.Sources;

namespace FoxData.Application.Canonical;

public sealed class WarReportCanonicalKernel(IWarReportCanonicalStore store)
{
    public Task<WarReportCanonicalResult?> GetByNormalizationRunAsync(
        NormalizationRunId normalizationRunId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(
            normalizationRunId.Value,
            nameof(normalizationRunId));

        return store.GetByNormalizationRunAsync(
            normalizationRunId,
            cancellationToken);
    }

    public Task<WarReportCanonicalResult> RecordAcceptedAsync(
        SourceParseRunId sourceParseRunId,
        string normalizerVersion,
        string capabilityKey,
        DateTimeOffset normalizationStartedAt,
        DateTimeOffset normalizationCompletedAt,
        ShardId shardId,
        FetchId representationFetchId,
        WarId warId,
        WarRegionId warRegionId,
        string sourceMapName,
        DateTimeOffset observedAt,
        CanonicalWarReportSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ValidateId(sourceParseRunId.Value, nameof(sourceParseRunId));
        ValidateId(shardId.Value, nameof(shardId));
        ValidateId(representationFetchId.Value, nameof(representationFetchId));
        ValidateId(warId.Value, nameof(warId));
        ValidateId(warRegionId.Value, nameof(warRegionId));
        ValidateRequiredText(
            normalizerVersion,
            128,
            nameof(normalizerVersion));
        ValidateRequiredText(
            capabilityKey,
            128,
            nameof(capabilityKey));
        ValidateRequiredText(
            sourceMapName,
            256,
            nameof(sourceMapName));

        if (normalizationCompletedAt < normalizationStartedAt)
        {
            throw new ArgumentException(
                "Normalization completion must not be earlier than its start.",
                nameof(normalizationCompletedAt));
        }

        ValidateSnapshot(snapshot);

        return store.RecordAcceptedAsync(
            new WarReportCanonicalWrite(
                sourceParseRunId,
                normalizerVersion,
                capabilityKey,
                normalizationStartedAt,
                normalizationCompletedAt,
                shardId,
                representationFetchId,
                warId,
                warRegionId,
                sourceMapName,
                observedAt,
                snapshot),
            cancellationToken);
    }

    private static void ValidateSnapshot(
        CanonicalWarReportSnapshot snapshot)
    {
        if (snapshot.TotalEnlistments is < 0 ||
            snapshot.ColonialCasualties is < 0 ||
            snapshot.WardenCasualties is < 0 ||
            snapshot.DayOfWar is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(snapshot),
                "War-report counters and dayOfWar must not be negative when supplied.");
        }
    }

    private static void ValidateRequiredText(
        string value,
        int maximum,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximum ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{parameterName} must be non-empty, already trimmed, and at most {maximum} characters.",
                parameterName);
        }
    }

    private static void ValidateId(
        Guid value,
        string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Identifier must not be empty.",
                parameterName);
        }
    }
}
