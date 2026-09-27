using FoxData.Core.Evidence;
using FoxData.Core.Sources;

namespace FoxData.Application.Canonical;

public sealed class WarCanonicalKernel(
    IWarCanonicalStore store,
    TimeProvider timeProvider)
{
    public Task<WarCanonicalResult> RecordAcceptedAsync(
        SourceParseRunId sourceParseRunId,
        ShardId shardId,
        FetchId representationFetchId,
        DateTimeOffset observedAt,
        CanonicalWarSnapshot snapshot,
        string normalizerVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateId(sourceParseRunId.Value, nameof(sourceParseRunId));
        ValidateId(shardId.Value, nameof(shardId));
        ValidateId(representationFetchId.Value, nameof(representationFetchId));
        ValidateSnapshot(snapshot);
        ValidateVersion(normalizerVersion);

        var startedAt = timeProvider.GetUtcNow();
        var completedAt = timeProvider.GetUtcNow();

        return store.RecordAcceptedAsync(
            new WarCanonicalWrite(
                sourceParseRunId,
                normalizerVersion,
                startedAt,
                completedAt,
                shardId,
                representationFetchId,
                observedAt,
                snapshot),
            cancellationToken);
    }

    private static void ValidateSnapshot(CanonicalWarSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.SourceWarId) ||
            snapshot.SourceWarId.Length > 256 ||
            !string.Equals(
                snapshot.SourceWarId,
                snapshot.SourceWarId.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "SourceWarId must be non-empty, already trimmed, and at most 256 characters.",
                nameof(snapshot));
        }

        if (snapshot.Winner is { Length: > 128 })
        {
            throw new ArgumentException(
                "Winner must be at most 128 characters.",
                nameof(snapshot));
        }

        if (snapshot.WarNumber is < 0 ||
            snapshot.RequiredVictoryTowns is < 0 ||
            snapshot.ShortRequiredVictoryTowns is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(snapshot),
                "War number and victory-town counts must not be negative.");
        }
    }

    private static void ValidateVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Normalizer version must be non-empty, already trimmed, and at most 128 characters.",
                nameof(value));
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
