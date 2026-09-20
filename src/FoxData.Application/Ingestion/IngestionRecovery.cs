using FoxData.Application.Diagnostics;
using FoxData.Core.Ingestion;

namespace FoxData.Application.Ingestion;

public enum RecoveryDisposition
{
    RequeuedNoAttempt,
    RequeuedBeforeExchange,
    MarkedUncertain,
    RepairedFromFetch,
}

public sealed record RecoveryItemResult(
    CollectionJobId JobId,
    IngestionAttemptId? AttemptId,
    RecoveryDisposition Disposition);

public sealed record RecoveryBatchResult(IReadOnlyList<RecoveryItemResult> Items)
{
    public int Count => Items.Count;
}

public sealed record IngestionRecoveryLimits
{
    public const int DefaultBatchSize = 32;
    public const int MaximumBatchSize = 1024;

    public IngestionRecoveryLimits(int batchSize = DefaultBatchSize)
    {
        if (batchSize is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                batchSize,
                $"Recovery batch size must be between 1 and {MaximumBatchSize}.");
        }

        BatchSize = batchSize;
    }

    public int BatchSize { get; }
}

public interface IIngestionRecoveryStore
{
    Task<RecoveryBatchResult> RecoverExpiredAsync(
        int batchSize,
        CancellationToken cancellationToken);
}

public sealed class IngestionRecovery
{
    private readonly IIngestionRecoveryStore _store;
    private readonly IngestionRecoveryLimits _limits;

    public IngestionRecovery(IIngestionRecoveryStore store)
        : this(store, new IngestionRecoveryLimits())
    {
    }

    public IngestionRecovery(
        IIngestionRecoveryStore store,
        IngestionRecoveryLimits limits)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public async Task<RecoveryBatchResult> RecoverExpiredAsync(
        CancellationToken cancellationToken = default)
    {
        using var activity = KernelTelemetry.ActivitySource.StartActivity("ingest.recover");

        var result = await _store.RecoverExpiredAsync(
            _limits.BatchSize,
            cancellationToken);

        foreach (var item in result.Items)
        {
            KernelTelemetry.RecoveryItems.Add(
                1,
                new KeyValuePair<string, object?>(
                    "outcome",
                    ToMetricOutcome(item.Disposition)));

            if (item.Disposition is RecoveryDisposition.MarkedUncertain)
            {
                KernelTelemetry.AttemptsUncertain.Add(1);
            }
        }

        activity?.SetTag("recovery.count", result.Count);

        return result;
    }

    private static string ToMetricOutcome(RecoveryDisposition disposition) =>
        disposition switch
        {
            RecoveryDisposition.RequeuedNoAttempt => "requeued_no_attempt",
            RecoveryDisposition.RequeuedBeforeExchange => "requeued_before_exchange",
            RecoveryDisposition.MarkedUncertain => "uncertain",
            RecoveryDisposition.RepairedFromFetch => "repaired_from_fetch",
            _ => "unknown",
        };
}
