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
    MapSnapshotKernel snapshots,
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

        var context = await ResolveContextAsync(
            normalized,
            shardId,
            observedAt,
            cancellationToken);
        if (context.Status != WarApiMapContextStatus.Resolved)
        {
            return Deferred(context.Reason ?? "map_context_unresolved");
        }

        return await EvaluateResolvedAsync(
            normalized,
            context,
            validationFetchId,
            cancellationToken);
    }

    public async Task<WarApiMapQualityEvaluation> Evaluate304Async(
        MapSnapshotResult normalized,
        ShardId shardId,
        FetchId validationFetchId,
        DateTimeOffset representationObservedAt,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalized);

        if (validationFetchId == normalized.Snapshot.RepresentationFetchId ||
            representationObservedAt > observedAt)
        {
            throw new CanonicalStateIntegrityException(
                "304 quality evaluation requires a later validation Fetch over an earlier reused representation.");
        }

        var representationContext = await ResolveContextAsync(
            normalized,
            shardId,
            representationObservedAt,
            cancellationToken);
        if (representationContext.Status !=
            WarApiMapContextStatus.Resolved)
        {
            return Deferred(
                representationContext.Reason
                ?? "representation_map_context_unresolved");
        }

        var validationContext = await ResolveContextAsync(
            normalized,
            shardId,
            observedAt,
            cancellationToken);
        if (validationContext.Status != WarApiMapContextStatus.Resolved)
        {
            return Deferred(
                validationContext.Reason
                ?? "validation_map_context_unresolved");
        }

        var representationRegion =
            representationContext.WarRegion
            ?? throw new CanonicalStateIntegrityException(
                "Resolved representation map context has no WarRegion.");
        var validationRegion =
            validationContext.WarRegion
            ?? throw new CanonicalStateIntegrityException(
                "Resolved validation map context has no WarRegion.");

        if (representationContext.WarId == validationContext.WarId &&
            representationRegion.Id != validationRegion.Id)
        {
            throw new CanonicalStateIntegrityException(
                "304 quality evaluation resolved one war to two different WarRegion identities for the same source map.");
        }

        // A cross-war 304 is valid continuity evidence when M5 proves the
        // new WarRegion at the validation boundary. Quality chronology and
        // baseline selection are scoped to that target WarRegion, so the
        // first result in the new war starts a fresh baseline chain.
        return await EvaluateResolvedAsync(
            normalized,
            validationContext,
            validationFetchId,
            cancellationToken);
    }

    private async Task<WarApiMapContextResolution> ResolveContextAsync(
        MapSnapshotResult normalized,
        ShardId shardId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        // Normalization is source-local; acceptance requires a separately
        // proven, war-scoped membership at the exact validation boundary.
        return await mapContext.ResolveAsync(
            shardId,
            normalized.Snapshot.SourceMapName,
            observedAt,
            cancellationToken);
    }

    private async Task<WarApiMapQualityEvaluation> EvaluateResolvedAsync(
        MapSnapshotResult normalized,
        WarApiMapContextResolution context,
        FetchId validationFetchId,
        CancellationToken cancellationToken)
    {
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

        MapSnapshotResult? baselineSnapshot = null;
        if (plan.Baseline is not null &&
            WarApiMapQualityPolicyEvaluator.RequiresBaselineSnapshot(profile))
        {
            baselineSnapshot = await snapshots.GetByIdAsync(
                plan.Baseline.MapSnapshotId,
                cancellationToken)
                ?? throw new CanonicalStateIntegrityException(
                    "Map quality baseline observation lost its durable MapSnapshot.");
        }

        var startedAt = timeProvider.GetUtcNow();
        var taxonomy = new WarApiMapTaxonomyInterpreter(
            WarApiMapTaxonomyRegistry.Get(profile.TaxonomyVersion));
        var evaluation = WarApiMapQualityPolicyEvaluator.Evaluate(
            profile,
            ToFeatureSnapshot(normalized),
            baselineSnapshot is null
                ? null
                : ToFeatureSnapshot(baselineSnapshot),
            region.SourceRegionId,
            plan.CurrentStructuralFingerprint,
            plan.BaselineStructuralFingerprint,
            taxonomy);
        var findings = evaluation.Findings
            .Select(ToFindingCandidate)
            .ToArray();
        var decision = ToDecision(evaluation.Decision);

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
        MapSnapshotResult source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new WarApiMapQualityFeatureSnapshot(
            source.Snapshot.SourceRegionId,
            source.Snapshot.SourceVersion,
            source.Snapshot.SourceLastUpdatedMs,
            source.Snapshot.SourceMapItemsArrayPresent,
            source.Snapshot.SourceMapTextItemsArrayPresent,
            source.Items
                .Select(
                    item => new WarApiMapQualityFeatureItem(
                        item.RawTeamId,
                        item.RawIconType,
                        item.X,
                        item.Y,
                        item.RawFlags,
                        item.RawViewDirection))
                .ToArray(),
            source.TextItems
                .Select(
                    item => new WarApiMapQualityFeatureTextItem(
                        item.Text,
                        item.X,
                        item.Y,
                        item.RawMapMarkerType))
                .ToArray());
    }

    private static MapQualityFindingCandidate ToFindingCandidate(
        WarApiMapQualityPolicyFinding finding)
    {
        return new MapQualityFindingCandidate(
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
                    "Unsupported War API quality finding effect."),
            },
            null,
            null,
            finding.DetailCode,
            finding.InputMetricsJson);
    }

    private static MapQualityDecision ToDecision(
        WarApiMapQualityPolicyDecision decision) =>
        decision switch
        {
            WarApiMapQualityPolicyDecision.Accepted =>
                MapQualityDecision.Accepted,
            WarApiMapQualityPolicyDecision.Suspect =>
                MapQualityDecision.Suspect,
            WarApiMapQualityPolicyDecision.Quarantined =>
                MapQualityDecision.Quarantined,
            _ => throw new InvalidOperationException(
                "Unsupported War API quality policy decision."),
        };

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
                    finding.RuleVersion),
                new KeyValuePair<string, object?>(
                    "effect",
                    finding.Effect.ToString().ToLowerInvariant()));
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
