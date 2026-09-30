using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Core.Runtime;

namespace FoxData.UnitTests;

public sealed class MapQualityDecisionSafetyTests
{
    [Theory]
    [InlineData(
        MapQualityDecision.Accepted,
        MapQualityFindingEffect.Suspect)]
    [InlineData(
        MapQualityDecision.Accepted,
        MapQualityFindingEffect.Quarantined)]
    [InlineData(
        MapQualityDecision.Suspect,
        MapQualityFindingEffect.Quarantined)]
    public void BlockingFindingCannotBeDowngraded(
        MapQualityDecision decision,
        MapQualityFindingEffect effect)
    {
        Assert.Throws<ArgumentException>(
            () => MapQualityDecisionSafety.Validate(
                Write(decision, effect)));
    }

    [Theory]
    [InlineData(
        MapQualityDecision.Accepted,
        MapQualityFindingEffect.Informational)]
    [InlineData(
        MapQualityDecision.Suspect,
        MapQualityFindingEffect.Informational)]
    [InlineData(
        MapQualityDecision.Quarantined,
        MapQualityFindingEffect.Suspect)]
    public void ConservativeDecisionRemainsPermitted(
        MapQualityDecision decision,
        MapQualityFindingEffect effect)
    {
        MapQualityDecisionSafety.Validate(Write(decision, effect));
    }

    private static MapQualityWrite Write(
        MapQualityDecision decision,
        MapQualityFindingEffect effect) =>
        new(
            MapSnapshotId.New(),
            WarRegionId.New(),
            FetchId.New(),
            "taxonomy@1",
            "policy@1",
            null,
            decision,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            [
                new MapQualityFindingCandidate(
                    "test.rule",
                    "test.rule@1",
                    "test.config@1",
                    effect,
                    null,
                    null,
                    null,
                    "{}"),
            ]);
}
