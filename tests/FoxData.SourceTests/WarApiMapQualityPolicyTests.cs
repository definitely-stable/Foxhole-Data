using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiMapQualityPolicyTests
{
    [Fact]
    public void V1ProfileLoadsEmbeddedContractAndKeepsStableIdentity()
    {
        var profile = WarApiMapQualityPolicyRegistry.Get(
            WarApiVersions.MapQualityPolicy);

        Assert.Equal("warapi-map-quality@1", profile.Version);
        Assert.Equal(WarApiCatalog.SourceKey, profile.Source);
        Assert.Equal(
            WarApiVersions.MapTaxonomy,
            profile.TaxonomyVersion);
        Assert.Equal(new DateOnly(2026, 9, 28), profile.ReviewedAt);
        Assert.Equal(8, profile.Rules.Count);

        Assert.Equal(
            profile.Rules.Count,
            profile.Rules
                .Select(rule => rule.Key)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(
            profile.Rules.Count,
            profile.Rules
                .Select(rule => rule.RuleVersion)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(
            profile.Rules.Count,
            profile.Rules
                .Select(rule => rule.ConfigurationVersion)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void V1AggregationMapsEffectsToTerminalQualityDecisions()
    {
        var aggregation = GetProfile().Aggregation;

        Assert.Equal(
            WarApiMapQualityPolicyDecision.Accepted,
            aggregation.NoFindingsDecision);
        Assert.Equal(
            WarApiMapQualityPolicyDecision.Accepted,
            aggregation.InformationalDecision);
        Assert.Equal(
            WarApiMapQualityPolicyDecision.Suspect,
            aggregation.SuspectDecision);
        Assert.Equal(
            WarApiMapQualityPolicyDecision.Quarantined,
            aggregation.QuarantinedDecision);
    }

    [Fact]
    public void V1StructuralRulesHaveExplicitVersionedEffects()
    {
        var profile = GetProfile();

        AssertRule(
            profile,
            "region-id.valid",
            WarApiMapQualityEffect.Quarantined);
        AssertRule(
            profile,
            "region-id.conflict",
            WarApiMapQualityEffect.Quarantined);
        AssertRule(
            profile,
            "coordinate.valid",
            WarApiMapQualityEffect.Quarantined);
        AssertRule(
            profile,
            "source-time.representable",
            WarApiMapQualityEffect.Suspect);
        AssertRule(
            profile,
            "schema.structure-changed",
            WarApiMapQualityEffect.Informational);
        AssertRule(
            profile,
            "taxonomy.unknown-icon",
            WarApiMapQualityEffect.Informational);
        AssertRule(
            profile,
            "taxonomy.unknown-team",
            WarApiMapQualityEffect.Informational);
        AssertRule(
            profile,
            "taxonomy.unknown-flag-bits",
            WarApiMapQualityEffect.Informational);
    }

    [Fact]
    public void V1StructuralParametersAreLoadedFromContract()
    {
        var profile = GetProfile();

        var coordinate = GetRule(profile, "coordinate.valid");
        Assert.Equal(
            0d,
            coordinate.Parameters["minimum"].GetDouble());
        Assert.Equal(
            1d,
            coordinate.Parameters["maximum"].GetDouble());
        Assert.True(
            coordinate.Parameters["requireFinite"].GetBoolean());

        var region = GetRule(profile, "region-id.valid");
        Assert.True(region.Parameters["allowNull"].GetBoolean());
        Assert.Equal(
            0,
            region.Parameters["minimum"].GetInt32());

        var sourceTime =
            GetRule(profile, "source-time.representable");
        Assert.True(
            sourceTime.Parameters["allowMissing"].GetBoolean());
        Assert.True(
            sourceTime.Parameters[
                "requireSafeUtcWhenPresent"].GetBoolean());

        Assert.Empty(
            GetRule(
                profile,
                "taxonomy.unknown-icon").Parameters);
    }

    [Fact]
    public void V1PolicyReferencesAnExecutableTaxonomyProfile()
    {
        var policy = GetProfile();
        var taxonomy =
            WarApiMapTaxonomyRegistry.Get(policy.TaxonomyVersion);

        Assert.Equal(policy.TaxonomyVersion, taxonomy.Version);
        Assert.Equal(policy.Source, taxonomy.Source);
    }

    [Fact]
    public void V2ProfileLoadsCalibratedRulesWithoutChangingRuntimeSelection()
    {
        var profile = WarApiMapQualityPolicyRegistry.Get(
            WarApiVersions.MapQualityPolicyV2);

        Assert.Equal(
            "warapi-map-quality@2",
            profile.Version);
        Assert.Equal(
            new DateOnly(2026, 10, 1),
            profile.ReviewedAt);
        Assert.Equal(15, profile.Rules.Count);
        Assert.Equal(
            WarApiVersions.MapQualityPolicyV1,
            WarApiVersions.MapQualityPolicy);
        Assert.Equal(
            WarApiVersions.MapQualityPolicyV2,
            WarApiVersions.LatestMapQualityPolicy);

        AssertRule(
            profile,
            "source-version.regression",
            WarApiMapQualityEffect.Informational);
        AssertRule(
            profile,
            "source-version.gap",
            WarApiMapQualityEffect.Informational);
        AssertRule(
            profile,
            "source-last-updated.regression",
            WarApiMapQualityEffect.Suspect);
        AssertRule(
            profile,
            "representation.near-empty",
            WarApiMapQualityEffect.Suspect);
        AssertRule(
            profile,
            "representation.mass-disappearance",
            WarApiMapQualityEffect.Suspect);
        AssertRule(
            profile,
            "representation.duplicate-occurrence",
            WarApiMapQualityEffect.Informational);
        AssertRule(
            profile,
            "ownership.restart-collapse",
            WarApiMapQualityEffect.Quarantined);
    }

    [Fact]
    public void V2ThresholdsComeFromFrozenPolicyData()
    {
        var profile = WarApiMapQualityPolicyRegistry.Get(
            WarApiVersions.MapQualityPolicyV2);

        var restart = GetRule(
            profile,
            "ownership.restart-collapse");
        Assert.Equal(
            12,
            restart.Parameters[
                "minimumBaselineItems"].GetInt32());
        Assert.Equal(
            0.5d,
            restart.Parameters[
                "minimumBaselineOwnedShare"].GetDouble());
        Assert.Equal(
            0.9d,
            restart.Parameters[
                "minimumCurrentNoneShare"].GetDouble());
        Assert.Equal(
            0.8d,
            restart.Parameters[
                "maximumItemCountRatio"].GetDouble());
        Assert.Equal(
            0.75d,
            restart.Parameters[
                "maximumDistinctIconTypeRatio"].GetDouble());

        var nearEmpty = GetRule(
            profile,
            "representation.near-empty");
        Assert.Equal(
            8,
            nearEmpty.Parameters[
                "minimumBaselineOccurrences"].GetInt32());
        Assert.Equal(
            0.2d,
            nearEmpty.Parameters[
                "maximumTotalOccurrenceRatio"].GetDouble());
    }

    [Fact]
    public void UnknownQualityPolicyVersionFailsClosed()
    {
        Assert.Throws<NotSupportedException>(
            () => WarApiMapQualityPolicyRegistry.Get(
                "warapi-map-quality@999"));
    }

    [Fact]
    public void UntrimmedQualityPolicyVersionIsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => WarApiMapQualityPolicyRegistry.Get(
                $" {WarApiVersions.MapQualityPolicy} "));
    }

    private static WarApiMapQualityPolicyProfile GetProfile() =>
        WarApiMapQualityPolicyRegistry.Get(
            WarApiVersions.MapQualityPolicy);

    private static WarApiMapQualityRuleProfile GetRule(
        WarApiMapQualityPolicyProfile profile,
        string key) =>
        Assert.Single(
            profile.Rules,
            rule => string.Equals(
                rule.Key,
                key,
                StringComparison.Ordinal));

    private static void AssertRule(
        WarApiMapQualityPolicyProfile profile,
        string key,
        WarApiMapQualityEffect effect)
    {
        var rule = GetRule(profile, key);

        Assert.Equal($"{key}@1", rule.RuleVersion);
        Assert.Equal(
            $"{key}-config@1",
            rule.ConfigurationVersion);
        Assert.Equal(effect, rule.Effect);
    }
}
