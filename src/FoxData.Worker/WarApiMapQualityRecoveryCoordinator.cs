using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Sources.WarApi;

namespace FoxData.Worker;

public sealed record WarApiMapQualityRecoveryResult(
    int TerminalCompleted,
    int Deferred,
    int VersionBlocked)
{
    public int ProgressCount => TerminalCompleted;
    public int OutstandingCount => Deferred + VersionBlocked;
}

public sealed class WarApiMapQualityRecoveryCoordinator(
    IMapQualityGapReader gaps,
    MapSnapshotKernel snapshots,
    WarApiMapQualityCoordinator quality)
{
    private const int BatchSize = 64;

    public Task<WarApiMapQualityRecoveryResult> RunOnceAsync(
        CancellationToken cancellationToken = default) =>
        RunOnceAsync(
            WarApiMapQualityTarget.Live,
            cancellationToken);

    public async Task<WarApiMapQualityRecoveryResult> RunOnceAsync(
        WarApiMapQualityTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        DateTimeOffset? afterObservedAt = null;
        FetchId? afterValidationFetchId = null;
        var terminalCompleted = 0;
        var deferred = 0;
        var versionBlocked = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await gaps.GetPendingAsync(
                WarApiCatalog.SourceKey,
                WarApiCoverageCapabilityPlans.QualityRecovery,
                target.TaxonomyVersion,
                target.PolicyVersion,
                afterObservedAt,
                afterValidationFetchId,
                BatchSize,
                cancellationToken);

            foreach (var candidate in batch)
            {
                var normalized =
                    await snapshots.GetByNormalizationRunAsync(
                        candidate.NormalizationRunId,
                        cancellationToken)
                    ?? throw new CanonicalStateIntegrityException(
                        "Quality-gap candidate lost its durable normalized snapshot.");

                var result = candidate.ValidationKind switch
                {
                    MapQualityGapValidationKind.BodyBearing200 =>
                        await quality.EvaluateAsync(
                            normalized,
                            candidate.ShardId,
                            candidate.ValidationFetchId,
                            candidate.ObservedAt,
                            target,
                            cancellationToken),
                    MapQualityGapValidationKind.NotModified304 =>
                        await quality.Evaluate304Async(
                            normalized,
                            candidate.ShardId,
                            candidate.ValidationFetchId,
                            candidate.RepresentationObservedAt,
                            candidate.ObservedAt,
                            target,
                            cancellationToken),
                    _ => throw new CanonicalStateIntegrityException(
                        "Unknown quality-gap validation kind."),
                };

                if (result.Status == WarApiMapQualityStatus.Deferred)
                {
                    if (string.Equals(
                            result.DeferredReason,
                            MapQualityDeferredReasons.LaterQualityAlreadyTerminal,
                            StringComparison.Ordinal))
                    {
                        versionBlocked++;
                        RecordTelemetry(
                            "version_blocked",
                            target.PolicyVersion);
                    }
                    else
                    {
                        deferred++;
                        RecordTelemetry(
                            "deferred",
                            target.PolicyVersion);
                    }
                }
                else
                {
                    terminalCompleted++;
                    RecordTelemetry(
                        "terminal",
                        target.PolicyVersion);
                }

                afterObservedAt = candidate.ObservedAt;
                afterValidationFetchId = candidate.ValidationFetchId;
            }

            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        return new WarApiMapQualityRecoveryResult(
            terminalCompleted,
            deferred,
            versionBlocked);
    }

    private static void RecordTelemetry(
        string outcome,
        string policyVersion)
    {
        WarApiTelemetry.MapQualityRecovery.Add(
            1,
            new KeyValuePair<string, object?>(
                "outcome",
                outcome),
            new KeyValuePair<string, object?>(
                "policy_version",
                policyVersion));
    }
}
