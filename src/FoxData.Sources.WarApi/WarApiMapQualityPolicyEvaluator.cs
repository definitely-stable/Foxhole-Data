using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FoxData.Sources.WarApi;

public sealed record WarApiMapQualityPolicyFinding(
    string RuleKey,
    string RuleVersion,
    string ConfigurationVersion,
    WarApiMapQualityEffect Effect,
    string DetailCode,
    string InputMetricsJson);

public sealed record WarApiMapQualityPolicyEvaluation(
    WarApiMapQualityPolicyDecision Decision,
    WarApiMapQualityFeatureVector Features,
    IReadOnlyList<WarApiMapQualityPolicyFinding> Findings);

public static class WarApiMapQualityPolicyEvaluator
{
    private static readonly HashSet<string> BaselineSnapshotRuleKeys =
        new(StringComparer.Ordinal)
        {
            "source-version.regression",
            "source-version.gap",
            "source-last-updated.regression",
            "representation.near-empty",
            "representation.mass-disappearance",
            "ownership.restart-collapse",
        };

    public static bool RequiresBaselineSnapshot(
        WarApiMapQualityPolicyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.Rules.Any(
            rule => BaselineSnapshotRuleKeys.Contains(rule.Key));
    }

    public static WarApiMapQualityPolicyEvaluation Evaluate(
        WarApiMapQualityPolicyProfile profile,
        WarApiMapQualityFeatureSnapshot current,
        WarApiMapQualityFeatureSnapshot? baseline,
        int? acceptedRegionId,
        string? currentStructuralFingerprint,
        string? baselineStructuralFingerprint,
        WarApiMapTaxonomyInterpreter taxonomy)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(taxonomy);

        var features = WarApiMapQualityFeatureExtractor.Extract(
            current,
            baseline,
            taxonomy);
        var rules = profile.Rules.ToDictionary(
            rule => rule.Key,
            StringComparer.Ordinal);
        var findings = new List<WarApiMapQualityPolicyFinding>();

        void Add(
            string key,
            string detail,
            params (string Key, object? Value)[] metrics)
        {
            var rule = rules[key];
            findings.Add(
                new WarApiMapQualityPolicyFinding(
                    rule.Key,
                    rule.RuleVersion,
                    rule.ConfigurationVersion,
                    rule.Effect,
                    detail,
                    Metrics(metrics)));
        }

        EvaluateStructural(
            current,
            acceptedRegionId,
            currentStructuralFingerprint,
            baselineStructuralFingerprint,
            features,
            rules,
            Add);

        if (rules.ContainsKey("source-version.regression"))
        {
            EvaluateCalibratedAnomalies(
                features,
                rules,
                Add);
        }

        return new WarApiMapQualityPolicyEvaluation(
            Aggregate(profile, findings),
            features,
            findings);
    }

    private static void EvaluateStructural(
        WarApiMapQualityFeatureSnapshot current,
        int? acceptedRegionId,
        string? currentStructuralFingerprint,
        string? baselineStructuralFingerprint,
        WarApiMapQualityFeatureVector features,
        IReadOnlyDictionary<string, WarApiMapQualityRuleProfile> rules,
        Action<string, string, (string Key, object? Value)[]> add)
    {
        var regionRule = rules["region-id.valid"];
        var allowNull =
            regionRule.Parameters["allowNull"].GetBoolean();
        var minimum =
            regionRule.Parameters["minimum"].GetInt32();
        if ((current.RegionId is null && !allowNull) ||
            current.RegionId is { } regionId &&
            regionId < minimum)
        {
            add(
                "region-id.valid",
                "invalid_region_id",
                [("count", 1)]);
        }

        if (current.RegionId is { } candidateRegionId &&
            acceptedRegionId is { } durableRegionId &&
            candidateRegionId != durableRegionId)
        {
            add(
                "region-id.conflict",
                "conflicting_region_id",
                [("count", 1)]);
        }

        if (features.InvalidCoordinateCount != 0)
        {
            add(
                "coordinate.valid",
                "invalid_coordinates",
                [("count", features.InvalidCoordinateCount)]);
        }

        var sourceTimeRule = rules["source-time.representable"];
        var allowMissing =
            sourceTimeRule.Parameters["allowMissing"].GetBoolean();
        if ((!allowMissing && current.SourceLastUpdated is null) ||
            current.SourceLastUpdated is { } sourceTime &&
            !RepresentableUnixMilliseconds(sourceTime))
        {
            add(
                "source-time.representable",
                "unrepresentable_source_timestamp",
                [("count", 1)]);
        }

        if (currentStructuralFingerprint is { } currentFingerprint &&
            baselineStructuralFingerprint is { } baselineFingerprint &&
            !string.Equals(
                currentFingerprint,
                baselineFingerprint,
                StringComparison.Ordinal))
        {
            add(
                "schema.structure-changed",
                "structural_fingerprint_changed",
                [("count", 1)]);
        }

        if (features.UnknownIconCount != 0)
        {
            add(
                "taxonomy.unknown-icon",
                "unknown_icon",
                [("count", features.UnknownIconCount)]);
        }

        if (features.UnknownTeamCount != 0)
        {
            add(
                "taxonomy.unknown-team",
                "unknown_team",
                [("count", features.UnknownTeamCount)]);
        }

        if (features.UnknownFlagOccurrenceCount != 0)
        {
            add(
                "taxonomy.unknown-flag-bits",
                "unknown_flag_bits",
                [("count", features.UnknownFlagOccurrenceCount)]);
        }
    }

    private static void EvaluateCalibratedAnomalies(
        WarApiMapQualityFeatureVector features,
        IReadOnlyDictionary<string, WarApiMapQualityRuleProfile> rules,
        Action<string, string, (string Key, object? Value)[]> add)
    {
        var versionRegression = rules["source-version.regression"];
        var versionRegressionThreshold =
            versionRegression.Parameters[
                "triggerBelowDelta"].GetDecimal();
        if (features.SourceVersionDelta is { } versionDelta &&
            versionDelta < versionRegressionThreshold)
        {
            add(
                versionRegression.Key,
                "source_version_regression",
                [
                    ("baselineSourceVersion",
                        features.BaselineSourceVersion),
                    ("sourceVersion", features.SourceVersion),
                    ("sourceVersionDelta", versionDelta),
                ]);
        }

        var versionGap = rules["source-version.gap"];
        var minimumMissingVersions =
            versionGap.Parameters[
                "minimumMissingVersions"].GetInt32();
        if (features.SourceVersionDelta is { } positiveDelta &&
            positiveDelta > 1)
        {
            var missingVersions = positiveDelta - 1m;
            if (missingVersions >= minimumMissingVersions)
            {
                add(
                    versionGap.Key,
                    "source_version_gap",
                    [
                        ("baselineSourceVersion",
                            features.BaselineSourceVersion),
                        ("sourceVersion", features.SourceVersion),
                        ("sourceVersionDelta", positiveDelta),
                        ("missingVersions", missingVersions),
                    ]);
            }
        }

        var sourceTime = rules["source-last-updated.regression"];
        var sourceTimeThreshold =
            sourceTime.Parameters[
                "triggerBelowDeltaMilliseconds"].GetDecimal();
        if (features.SourceLastUpdatedDeltaMilliseconds is
            { } lastUpdatedDelta &&
            lastUpdatedDelta < sourceTimeThreshold)
        {
            add(
                sourceTime.Key,
                "source_last_updated_regression",
                [
                    ("baselineSourceLastUpdated",
                        features.BaselineSourceLastUpdated),
                    ("sourceLastUpdated",
                        features.SourceLastUpdated),
                    ("sourceLastUpdatedDeltaMilliseconds",
                        lastUpdatedDelta),
                ]);
        }

        var nearEmpty = rules["representation.near-empty"];
        var baselineOccurrences =
            (features.BaselineItemCount ?? 0) +
            (features.BaselineTextItemCount ?? 0);
        var minimumBaselineOccurrences =
            nearEmpty.Parameters[
                "minimumBaselineOccurrences"].GetInt32();
        var maximumTotalRatio =
            nearEmpty.Parameters[
                "maximumTotalOccurrenceRatio"].GetDouble();
        if (features.HasBaseline &&
            baselineOccurrences >= minimumBaselineOccurrences &&
            features.TotalOccurrenceCountRatio is { } totalRatio &&
            totalRatio <= maximumTotalRatio)
        {
            add(
                nearEmpty.Key,
                "near_empty_representation",
                [
                    ("baselineOccurrenceCount",
                        baselineOccurrences),
                    ("currentOccurrenceCount",
                        features.ItemCount +
                        features.TextItemCount),
                    ("totalOccurrenceCountRatio",
                        totalRatio),
                ]);
        }

        var disappearance =
            rules["representation.mass-disappearance"];
        var minimumBaselineItems =
            disappearance.Parameters[
                "minimumBaselineItems"].GetInt32();
        var maximumItemRatio =
            disappearance.Parameters[
                "maximumItemCountRatio"].GetDouble();
        if (features.HasBaseline &&
            features.BaselineItemCount is { } baselineItems &&
            baselineItems >= minimumBaselineItems &&
            features.ItemCountRatio is { } itemRatio &&
            itemRatio <= maximumItemRatio)
        {
            add(
                disappearance.Key,
                "mass_item_disappearance",
                [
                    ("baselineItemCount", baselineItems),
                    ("itemCount", features.ItemCount),
                    ("itemCountRatio", itemRatio),
                ]);
        }

        var duplicate =
            rules["representation.duplicate-occurrence"];
        var duplicateExcess =
            features.DuplicateItemExcessCount +
            features.DuplicateTextExcessCount;
        var minimumDuplicateExcess =
            duplicate.Parameters[
                "minimumExcessCount"].GetInt32();
        if (duplicateExcess >= minimumDuplicateExcess)
        {
            add(
                duplicate.Key,
                "duplicate_occurrence",
                [
                    ("itemDuplicateExcessCount",
                        features.DuplicateItemExcessCount),
                    ("textDuplicateExcessCount",
                        features.DuplicateTextExcessCount),
                    ("totalDuplicateExcessCount",
                        duplicateExcess),
                ]);
        }

        EvaluateRestartCollapse(features, rules, add);
    }

    private static void EvaluateRestartCollapse(
        WarApiMapQualityFeatureVector features,
        IReadOnlyDictionary<string, WarApiMapQualityRuleProfile> rules,
        Action<string, string, (string Key, object? Value)[]> add)
    {
        var rule = rules["ownership.restart-collapse"];
        if (!features.HasBaseline ||
            features.BaselineItemCount is not { } baselineItems ||
            features.BaselineOwnedTeamShare is not { } baselineOwned ||
            features.NoneShare is not { } currentNone ||
            features.NoneShareDelta is not { } noneDelta ||
            features.OwnedTeamShare is not { } currentOwned ||
            features.OwnedTeamShareDelta is not { } ownedDelta)
        {
            return;
        }

        var minimumBaselineItems =
            rule.Parameters["minimumBaselineItems"].GetInt32();
        var minimumBaselineOwned =
            rule.Parameters[
                "minimumBaselineOwnedShare"].GetDouble();
        var minimumCurrentNone =
            rule.Parameters[
                "minimumCurrentNoneShare"].GetDouble();
        var minimumNoneIncrease =
            rule.Parameters[
                "minimumNoneShareIncrease"].GetDouble();
        var maximumCurrentOwned =
            rule.Parameters[
                "maximumCurrentOwnedShare"].GetDouble();
        var minimumOwnedDrop =
            rule.Parameters[
                "minimumOwnedShareDrop"].GetDouble();

        var ownedDrop = -ownedDelta;
        if (baselineItems < minimumBaselineItems ||
            baselineOwned < minimumBaselineOwned ||
            currentNone < minimumCurrentNone ||
            noneDelta < minimumNoneIncrease ||
            currentOwned > maximumCurrentOwned ||
            ownedDrop < minimumOwnedDrop)
        {
            return;
        }

        var corroboratingSignals = 0;
        var versionRegressionSignal =
            rule.Parameters[
                "versionRegressionCountsAsCorroboratingSignal"]
                .GetBoolean() &&
            features.SourceVersionDelta is { } versionDelta &&
            versionDelta < 0;
        if (versionRegressionSignal)
        {
            corroboratingSignals++;
        }

        var maximumItemRatio =
            rule.Parameters[
                "maximumItemCountRatio"].GetDouble();
        var itemCollapseSignal =
            features.ItemCountRatio is { } itemRatio &&
            itemRatio <= maximumItemRatio;
        if (itemCollapseSignal)
        {
            corroboratingSignals++;
        }

        var maximumIconRatio =
            rule.Parameters[
                "maximumDistinctIconTypeRatio"].GetDouble();
        var iconCollapseSignal =
            features.DistinctIconTypeRatio is { } iconRatio &&
            iconRatio <= maximumIconRatio;
        if (iconCollapseSignal)
        {
            corroboratingSignals++;
        }

        var minimumSignals =
            rule.Parameters[
                "minimumCorroboratingSignals"].GetInt32();
        if (corroboratingSignals < minimumSignals)
        {
            return;
        }

        add(
            rule.Key,
            "restart_ownership_collapse",
            [
                ("baselineItemCount", baselineItems),
                ("baselineOwnedTeamShare", baselineOwned),
                ("currentNoneShare", currentNone),
                ("noneShareIncrease", noneDelta),
                ("currentOwnedTeamShare", currentOwned),
                ("ownedTeamShareDrop", ownedDrop),
                ("itemCountRatio", features.ItemCountRatio),
                ("distinctIconTypeRatio",
                    features.DistinctIconTypeRatio),
                ("versionRegressionSignal",
                    versionRegressionSignal),
                ("itemCollapseSignal", itemCollapseSignal),
                ("iconCollapseSignal", iconCollapseSignal),
                ("corroboratingSignals", corroboratingSignals),
            ]);
    }

    private static WarApiMapQualityPolicyDecision Aggregate(
        WarApiMapQualityPolicyProfile profile,
        IReadOnlyList<WarApiMapQualityPolicyFinding> findings)
    {
        if (findings.Count == 0)
        {
            return profile.Aggregation.NoFindingsDecision;
        }

        if (findings.Any(
                finding =>
                    finding.Effect ==
                    WarApiMapQualityEffect.Quarantined))
        {
            return profile.Aggregation.QuarantinedDecision;
        }

        if (findings.Any(
                finding =>
                    finding.Effect ==
                    WarApiMapQualityEffect.Suspect))
        {
            return profile.Aggregation.SuspectDecision;
        }

        return profile.Aggregation.InformationalDecision;
    }

    private static bool RepresentableUnixMilliseconds(long value)
    {
        try
        {
            _ = DateTimeOffset.FromUnixTimeMilliseconds(value);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string Metrics(
        params (string Key, object? Value)[] values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in values
                         .OrderBy(
                             item => item.Key,
                             StringComparer.Ordinal))
            {
                WriteMetric(writer, key, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteMetric(
        Utf8JsonWriter writer,
        string key,
        object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(key);
                break;
            case int number:
                writer.WriteNumber(key, number);
                break;
            case long number:
                writer.WriteNumber(key, number);
                break;
            case decimal number:
                writer.WriteNumber(key, number);
                break;
            case double number:
                writer.WriteNumber(key, number);
                break;
            case bool boolean:
                writer.WriteBoolean(key, boolean);
                break;
            case string text:
                writer.WriteString(key, text);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported deterministic quality metric type '{value.GetType().FullName}'.");
        }
    }
}
