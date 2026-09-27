using FoxData.Core.Evidence;
using FoxData.Core.Runtime;
using FoxData.Core.Sources;

namespace FoxData.Application.Canonical;

public sealed class RegionCanonicalKernel(IRegionCanonicalStore store)
{
    public Task<RegionCanonicalResult> RecordAcceptedAsync(
        SourceParseRunId sourceParseRunId,
        string normalizerVersion,
        DateTimeOffset normalizationStartedAt,
        DateTimeOffset normalizationCompletedAt,
        ShardId shardId,
        FetchId representationFetchId,
        WarId warId,
        DateTimeOffset observedAt,
        IReadOnlyList<RegionMembershipCandidate> memberships,
        CancellationToken cancellationToken = default)
    {
        ValidateId(sourceParseRunId.Value, nameof(sourceParseRunId));
        ValidateId(shardId.Value, nameof(shardId));
        ValidateId(representationFetchId.Value, nameof(representationFetchId));
        ValidateId(warId.Value, nameof(warId));
        ValidateRequiredText(normalizerVersion, 128, nameof(normalizerVersion));
        ArgumentNullException.ThrowIfNull(memberships);

        if (normalizationCompletedAt < normalizationStartedAt)
        {
            throw new ArgumentException(
                "Normalization completion must not be earlier than its start.",
                nameof(normalizationCompletedAt));
        }

        var sourceNames = new HashSet<string>(StringComparer.Ordinal);
        var canonicalKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var membership in memberships)
        {
            ArgumentNullException.ThrowIfNull(membership);
            ValidateRequiredText(
                membership.CanonicalKey,
                256,
                nameof(membership.CanonicalKey));
            ValidateRequiredText(
                membership.DisplayName,
                256,
                nameof(membership.DisplayName));
            ValidateRequiredText(
                membership.SourceMapName,
                256,
                nameof(membership.SourceMapName));

            if (membership.SourceRegionId is < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(memberships),
                    "SourceRegionId must not be negative.");
            }

            if (!sourceNames.Add(membership.SourceMapName))
            {
                throw new ArgumentException(
                    $"Duplicate source map name '{membership.SourceMapName}' is not allowed.",
                    nameof(memberships));
            }

            if (!canonicalKeys.Add(membership.CanonicalKey))
            {
                throw new ArgumentException(
                    $"Duplicate canonical region key '{membership.CanonicalKey}' is not allowed.",
                    nameof(memberships));
            }
        }

        return store.RecordAcceptedAsync(
            new RegionCanonicalWrite(
                sourceParseRunId,
                normalizerVersion,
                normalizationStartedAt,
                normalizationCompletedAt,
                shardId,
                representationFetchId,
                warId,
                observedAt,
                memberships),
            cancellationToken);
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

    private static void ValidateId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Identifier must not be empty.",
                parameterName);
        }
    }
}
