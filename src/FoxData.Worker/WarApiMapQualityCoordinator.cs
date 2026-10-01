using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Core.Sources;
using FoxData.Sources.WarApi;

namespace FoxData.Worker;

public enum WarApiMapQualityStatus
{
    Accepted,
    Suspect,
    Quarantined,
    Deferred,
}

public sealed record WarApiMapQualityEvaluation(
    WarApiMapQualityStatus Status,
    MapQualityResult? Quality,
    string? DeferredReason);

public sealed class WarApiMapQualityCoordinator(
    WarApiMapContextResolver mapContext,
    IMapQualityOrderingReader ordering,
    IMapQualityStore qualityStore,
    MapQualityKernel quality,
    TimeProvider timeProvider)
{
    public async Task<WarApiMapQualityEvaluation> EvaluateAsync(
        MapSnapshotResult normalized,
        ShardId shardId,
        FetchId validationFetchId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalized);

        // Normalization is source-local; acceptance requires a separately
        // proven, war-scoped membership at this exact observation boundary.
        var context = await mapContext.ResolveAsync(
            shardId,
            normalized.Snapshot.SourceMapName,
            observedAt,
            cancellationToken);
        if (context.Status != WarApiMapContextStatus.Resolved)
        {
            return Deferred(context.Reason ?? "map_context_unresolved");
        }

        var region = context.WarRegion
            ?? throw new CanonicalStateIntegrityException(
                "Resolved map quality context has no WarRegion.");

        var profile = WarApiMapQualityPolicyRegistry.Get(
            WarApiVersions.MapQualityPolicy);
        var existing = await qualityStore.GetAsync(
            normalized.Snapshot.Id,
            region.Id,
            validationFetchId,
            profile.TaxonomyVersion,
            profile.Version,
            cancellationToken);
        if (existing is not null)
        {
            return Complete(existing);
        }

        var plan = await ordering.GetPlanAsync(
            normalized.Snapshot.Id,
            region.Id,
            validationFetchId,
            profile.TaxonomyVersion,
            profile.Version,
            cancellationToken);
        if (plan.Status == MapQualityOrderingStatus.Deferred)
        {
            return Deferred(plan.DeferredReason!);
        }

        var startedAt = timeProvider.GetUtcNow();
        var taxonomy = new WarApiMapTaxonomyInterpreter(
            WarApiMapTaxonomyRegistry.Get(profile.TaxonomyVersion));
        var sourceEvaluation =
            WarApiMapQualityPolicyEvaluator.Evaluate(
                profile,
                ToFeatureSnapshot(normalized),
                baseline: null,
                region.SourceRegionId,
                plan.CurrentStructuralFingerprint,
                plan.BaselineStructuralFingerprint,
                taxonomy);
        var findings = sourceEvaluation.Findings
            .Select(
                finding => new MapQualityFindingCandidate(
                    finding.RuleKey,
                    finding.RuleVersion,
                    finding.ConfigurationVersion,
                    finding.Effect switch
                    {
                        WarApiMapQualityEffect.Informational =>
                            MapQualityFindingEffect.Informational,
                        WarApiMapQualityEffect.Suspect =>
                            MapQualityFindingEffect.Suspect,
                        WarApiMapQualityEffect.Quarantined =>
                            MapQualityFindingEffect.Quarantined,
                        _ => throw new InvalidOperationException(
                            "Unsupported source quality finding effect."),
                    },
                    null,
                    null,
                    finding.DetailCode,
                    finding.InputMetricsJson))
            .ToArray();
        var decision = sourceEvaluation.Decision switch
        {
            WarApiMapQualityPolicyDecision.Accepted =>
                MapQualityDecision.Accepted,
            WarApiMapQualityPolicyDecision.Suspect =>
                MapQualityDecision.Suspect,
            WarApiMapQualityPolicyDecision.Quarantined =>
                MapQualityDecision.Quarantined,
            _ => throw new InvalidOperationException(
                "Unsupported source quality decision."),
        };

        // The store recomputes chronology/baseline inside the locked
        // transaction. A stale preliminary evaluation is never committed.
        try
        {
            var result = await quality.RecordAsync(
                new MapQualityWrite(
                    normalized.Snapshot.Id,
                    region.Id,
                    validationFetchId,
                    profile.TaxonomyVersion,
                    profile.Version,
                    plan.Baseline?.Id,
                    decision,
                    startedAt,
                    timeProvider.GetUtcNow(),
                    findings),
                cancellationToken);
            return Complete(result);
        }
        catch (MapQualityOrderingDeferredException exception)
        {
            return Deferred(exception.Reason);
        }
    }

    private static WarApiMapQualityFeatureSnapshot ToFeatureSnapshot(
        MapSnapshotResult normalized) =>
        new(
            normalized.Snapshot.SourceRegionId,
            normalized.Snapshot.SourceVersion,
            normalized.Snapshot.SourceLastUpdatedMs,
            normalized.Snapshot.SourceMapItemsArrayPresent,
            normalized.Snapshot.SourceMapTextItemsArrayPresent,
            normalized.Items
                .Select(
                    item => new WarApiMapQualityFeatureItem(
                        item.RawTeamId,
                        item.RawIconType,
                        item.X,
                        item.Y,
                        item.RawFlags,
                        item.RawViewDirection))
                .ToArray(),
            normalized.TextItems
                .Select(
                    item => new WarApiMapQualityFeatureTextItem(
                        item.Text,
                        item.X,
                        item.Y,
                        item.RawMapMarkerType))
                .ToArray());

    private static WarApiMapQualityEvaluation Complete(
        MapQualityResult result)
    {
        var status = result.Run.Decision switch
        {
            MapQualityDecision.Accepted => WarApiMapQualityStatus.Accepted,
            MapQualityDecision.Suspect => WarApiMapQualityStatus.Suspect,
            MapQualityDecision.Quarantined =>
                WarApiMapQualityStatus.Quarantined,
            _ => throw new CanonicalStateIntegrityException(
                "Unknown durable map quality decision."),
        };
        foreach (var finding in result.Findings)
        {
            WarApiTelemetry.MapQualityRuleHits.Add(
                1,
                new KeyValuePair<string, object?>(
                    "rule_key",
                    finding.RuleKey),
                new KeyValuePair<string, object?>(
                    "rule_version",
                    finding.RuleVersion));
        }

        WarApiTelemetry.MapQualityRuns.Add(
            1,
            new KeyValuePair<string, object?>(
                "decision",
                result.Run.Decision.ToString().ToLowerInvariant()),
            new KeyValuePair<string, object?>(
                "capability",
                result.Observation?.Kind.ToString().ToLowerInvariant()
                ?? "not_accepted"));
        return new WarApiMapQualityEvaluation(status, result, null);
    }

    private static WarApiMapQualityEvaluation Deferred(string reason)
    {
        WarApiTelemetry.MapQualityDeferred.Add(1);
        return new WarApiMapQualityEvaluation(
            WarApiMapQualityStatus.Deferred,
            null,
            reason);
    }
}
