using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiMapQualityPolicyEvaluatorTests
{
    private readonly WarApiMapQualityPolicyProfile _profile =
        WarApiMapQualityPolicyRegistry.Get(
            WarApiVersions.MapQualityPolicyV2);
    private readonly WarApiMapTaxonomyInterpreter _taxonomy =
        new(
            WarApiMapTaxonomyRegistry.Get(
                WarApiVersions.MapTaxonomy));

    [Theory]
    [InlineData(
        "warapi-92-restart-mass-none.json",
        "healthy-dynamic-baseline.json",
        WarApiMapQualityPolicyDecision.Quarantined,
        "ownership.restart-collapse")]
    [InlineData(
        "warapi-120-restart-transient.json",
        "healthy-dynamic-baseline.json",
        WarApiMapQualityPolicyDecision.Quarantined,
        "ownership.restart-collapse")]
    [InlineData(
        "source-version-regression.json",
        "healthy-dynamic-baseline.json",
        WarApiMapQualityPolicyDecision.Accepted,
        "source-version.regression")]
    [InlineData(
        "last-updated-regression.json",
        "healthy-dynamic-baseline.json",
        WarApiMapQualityPolicyDecision.Suspect,
        "source-last-updated.regression")]
    [InlineData(
        "source-version-gap.json",
        "healthy-dynamic-baseline.json",
        WarApiMapQualityPolicyDecision.Accepted,
        "source-version.gap")]
    [InlineData(
        "near-empty-representation.json",
        "healthy-dynamic-baseline.json",
        WarApiMapQualityPolicyDecision.Suspect,
        "representation.near-empty")]
    [InlineData(
        "mass-disappearance.json",
        "healthy-dynamic-baseline.json",
        WarApiMapQualityPolicyDecision.Suspect,
        "representation.mass-disappearance")]
    public void CalibratedCasesProduceExpectedDecisionAndRule(
        string currentFile,
        string baselineFile,
        WarApiMapQualityPolicyDecision expectedDecision,
        string requiredRule)
    {
        var current = Load(currentFile);
        var baseline = Load(baselineFile);

        var result = Evaluate(current, baseline);

        Assert.Equal(expectedDecision, result.Decision);
        Assert.Contains(
            result.Findings,
            finding => finding.RuleKey == requiredRule);
    }

    [Fact]
    public void NoneShareAloneCannotTriggerRestartQuarantine()
    {
        var baseline = Load("war-start-neutral.json");
        var current = Load("stable-neutral-continuation.json");

        var result = Evaluate(current, baseline);

        Assert.Equal(
            WarApiMapQualityPolicyDecision.Accepted,
            result.Decision);
        Assert.DoesNotContain(
            result.Findings,
            finding =>
                finding.RuleKey == "ownership.restart-collapse");
    }

    [Fact]
    public void UnknownTaxonomyOnHealthyShapeRemainsAccepted()
    {
        var baseline = Load("healthy-dynamic-baseline.json");
        var current = Load(
            "unknown-taxonomy-healthy-shape.json");

        var result = Evaluate(current, baseline);

        Assert.Equal(
            WarApiMapQualityPolicyDecision.Accepted,
            result.Decision);
        Assert.Contains(
            result.Findings,
            finding =>
                finding.RuleKey == "taxonomy.unknown-icon");
        Assert.Contains(
            result.Findings,
            finding =>
                finding.RuleKey == "taxonomy.unknown-team");
        Assert.Contains(
            result.Findings,
            finding =>
                finding.RuleKey ==
                "taxonomy.unknown-flag-bits");
        Assert.DoesNotContain(
            result.Findings,
            finding =>
                finding.RuleKey ==
                "ownership.restart-collapse");
    }

    [Fact]
    public void DuplicateOccurrenceIsDiagnosticAndPreserved()
    {
        var current = Load(
            "warapi-115-duplicate-rocket-target.json");

        var result = Evaluate(current, baseline: null);

        Assert.Equal(
            WarApiMapQualityPolicyDecision.Accepted,
            result.Decision);
        Assert.Contains(
            result.Findings,
            finding =>
                finding.RuleKey ==
                "representation.duplicate-occurrence");
        Assert.Equal(
            1,
            result.Features.DuplicateItemExcessCount);
    }

    [Fact]
    public void StructuralMetricsRemainCompatibleWithPolicyV1()
    {
        var current = Load("invalid-coordinate.json");

        var result = Evaluate(current, baseline: null);
        var finding = Assert.Single(
            result.Findings,
            item => item.RuleKey == "coordinate.valid");

        Assert.Equal(
            """{"count":1}""",
            finding.InputMetricsJson);
    }

    [Fact]
    public void EvaluationIsDeterministic()
    {
        var current = Load(
            "warapi-92-restart-mass-none.json");
        var baseline = Load("healthy-dynamic-baseline.json");

        var first = Evaluate(current, baseline);
        var second = Evaluate(current, baseline);

        Assert.Equal(first.Decision, second.Decision);
        Assert.Equal(first.Features, second.Features);
        Assert.Equal(
            first.Findings.Select(
                item => item.InputMetricsJson),
            second.Findings.Select(
                item => item.InputMetricsJson));
    }

    private WarApiMapQualityPolicyEvaluation Evaluate(
        LoadedFixture current,
        LoadedFixture? baseline)
    {
        return WarApiMapQualityPolicyEvaluator.Evaluate(
            _profile,
            current.Snapshot,
            baseline?.Snapshot,
            baseline?.Snapshot.RegionId ??
                current.Snapshot.RegionId,
            current.StructuralFingerprint,
            baseline?.StructuralFingerprint,
            _taxonomy);
    }

    private static LoadedFixture Load(string file)
    {
        var capability = file.StartsWith(
            "warapi-77-static-",
            StringComparison.Ordinal)
            ? WarApiCapabilities.StaticMapState
            : WarApiCapabilities.DynamicMapState;
        var parsed = new WarApiParser().Parse(
            capability,
            File.ReadAllBytes(FixturePath(file)));

        Assert.True(parsed.Parsed);
        return new LoadedFixture(
            WarApiMapQualityFeatureSnapshot.FromDto(
                Assert.IsType<WarApiMapDataDto>(
                    parsed.Value)),
            Assert.IsType<string>(
                parsed.StructuralFingerprint));
    }

    private static string FixturePath(string file) =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "M6",
            file);

    private sealed record LoadedFixture(
        WarApiMapQualityFeatureSnapshot Snapshot,
        string StructuralFingerprint);
}
