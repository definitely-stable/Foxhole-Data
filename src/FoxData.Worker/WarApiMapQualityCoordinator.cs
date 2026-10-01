using System.Text.Json;
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
        var findings = EvaluateStructuralFindings(
            normalized,
            region,
            plan,
            profile,
            taxonomy);
        var decision = findings.Any(
            finding => finding.Effect == MapQualityFindingEffect.Quarantined)
            ? MapQualityDecision.Quarantined
            : findings.Any(
                finding => finding.Effect == MapQualityFindingEffect.Suspect)
                ? MapQualityDecision.Suspect
                : MapQualityDecision.Accepted;

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

    private static IReadOnlyList<MapQualityFindingCandidate>
        EvaluateStructuralFindings(
        MapSnapshotResult normalized,
        WarRegionDescriptor region,
        MapQualityOrderingPlan plan,
        WarApiMapQualityPolicyProfile profile,
        WarApiMapTaxonomyInterpreter taxonomy)
    {
        var snapshot = normalized.Snapshot;
        var findings = new List<MapQualityFindingCandidate>();
        var rules = profile.Rules.ToDictionary(
            rule => rule.Key,
            StringComparer.Ordinal);

        void Add(string key, string detail, int count)
        {
            var rule = rules[key];
            var effect = rule.Effect switch
            {
                WarApiMapQualityEffect.Informational =>
                    MapQualityFindingEffect.Informational,
                WarApiMapQualityEffect.Suspect =>
                    MapQualityFindingEffect.Suspect,
                WarApiMapQualityEffect.Quarantined =>
                    MapQualityFindingEffect.Quarantined,
                _ => throw new InvalidOperationException(
                    "Unsupported versioned map quality effect."),
            };
            findings.Add(
                new MapQualityFindingCandidate(
                    rule.Key,
                    rule.RuleVersion,
                    rule.ConfigurationVersion,
                    effect,
                    null,
                    null,
                    detail,
                    JsonSerializer.Serialize(new { count })));
        }

        var regionRule = rules["region-id.valid"];
        var minimum = regionRule.Parameters["minimum"].GetInt32();
        var allowNull = regionRule.Parameters["allowNull"].GetBoolean();
        if ((snapshot.SourceRegionId is null && !allowNull) ||
            snapshot.SourceRegionId is { } sourceId &&
            sourceId < minimum)
        {
            Add("region-id.valid", "invalid_region_id", 1);
        }

        if (snapshot.SourceRegionId is { } candidateId &&
            region.SourceRegionId is { } acceptedId &&
            candidateId != acceptedId)
        {
            Add("region-id.conflict", "conflicting_region_id", 1);
        }

        var coordinateRule = rules["coordinate.valid"];
        var coordinateMinimum =
            coordinateRule.Parameters["minimum"].GetDouble();
        var coordinateMaximum =
            coordinateRule.Parameters["maximum"].GetDouble();
        var requireFinite =
            coordinateRule.Parameters["requireFinite"].GetBoolean();
        bool Invalid(double? coordinate) =>
            coordinate is { } value &&
            (value < coordinateMinimum ||
             value > coordinateMaximum ||
             requireFinite && !double.IsFinite(value));

        var invalidCoordinates = normalized.Items.Count(
            item => Invalid(item.X) || Invalid(item.Y)) +
            normalized.TextItems.Count(
                item => Invalid(item.X) || Invalid(item.Y));
        if (invalidCoordinates != 0)
        {
            Add("coordinate.valid", "invalid_coordinates",
                invalidCoordinates);
        }

        var timeRule = rules["source-time.representable"];
        var allowMissingTime =
            timeRule.Parameters["allowMissing"].GetBoolean();
        if ((!allowMissingTime &&
             snapshot.SourceLastUpdatedMs is null) ||
            snapshot.SourceLastUpdatedMs is not null &&
            snapshot.SourceUpdatedAt is null)
        {
            Add("source-time.representable",
                "unrepresentable_source_timestamp", 1);
        }

        if (plan.Baseline is not null &&
            plan.CurrentStructuralFingerprint is { } current &&
            plan.BaselineStructuralFingerprint is { } previous &&
            !string.Equals(current, previous, StringComparison.Ordinal))
        {
            Add("schema.structure-changed",
                "structural_fingerprint_changed", 1);
        }

        var unknownIcons = normalized.Items.Count(
            item => taxonomy.InterpretIcon(item.RawIconType).Status ==
                WarApiMapTaxonomyLookupStatus.Unknown);
        if (unknownIcons != 0)
        {
            Add("taxonomy.unknown-icon", "unknown_icon",
                unknownIcons);
        }

        var unknownTeams = normalized.Items.Count(
            item => taxonomy.InterpretTeam(item.RawTeamId).Status ==
                WarApiMapTaxonomyLookupStatus.Unknown);
        if (unknownTeams != 0)
        {
            Add("taxonomy.unknown-team", "unknown_team",
                unknownTeams);
        }

        var unknownFlags = normalized.Items.Count(
            item => taxonomy.InterpretFlags(item.RawFlags).Status ==
                WarApiMapFlagInterpretationStatus.ContainsUnknownBits);
        if (unknownFlags != 0)
        {
            Add("taxonomy.unknown-flag-bits",
                "unknown_flag_bits", unknownFlags);
        }

        return findings;
    }

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
