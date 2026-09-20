using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiMeasurementProbePolicyTests
{
    [Fact]
    public void DisabledProbeSelectsNothingAndPreservesCadence()
    {
        var endpoint = WarApiCatalog.DynamicMap("DeadLandsHex");
        var profile = WarApiMeasurementProbeProfile.Disabled;

        var selected = WarApiMeasurementProbePolicy.SelectMaps(
            profile,
            "live-1",
            ["DeadLandsHex", "MarbanHollow"]);

        var cadence = WarApiMeasurementProbePolicy.ResolveCadence(
            profile,
            "live-1",
            endpoint,
            ["DeadLandsHex", "MarbanHollow"],
            TimeSpan.FromMinutes(1));

        Assert.Empty(selected);
        Assert.Equal(TimeSpan.FromMinutes(1), cadence);
        Assert.Equal(
            "warapi-poll@1/warapi-bootstrap-profile@1",
            WarApiMeasurementProbePolicy.SchedulingPolicy(
                "warapi-poll@1/warapi-bootstrap-profile@1",
                profile,
                selected: false));
    }

    [Fact]
    public void SelectionIsDeterministicBoundedAndOrderIndependent()
    {
        var profile = EnabledProfile(maxMaps: 3);
        var first = WarApiMeasurementProbePolicy.SelectMaps(
            profile,
            "live-1",
            [
                "DeadLandsHex",
                "MarbanHollow",
                "RedRiverHex",
                "StonecradleHex",
                "CallahansPassage",
                "HomeRegionC",
                "HomeRegionW",
            ]);
        var second = WarApiMeasurementProbePolicy.SelectMaps(
            profile,
            "live-1",
            [
                "HomeRegionW",
                "CallahansPassage",
                "StonecradleHex",
                "RedRiverHex",
                "MarbanHollow",
                "DeadLandsHex",
                "HomeRegionC",
                "DeadLandsHex",
            ]);

        Assert.Equal(3, first.Count);
        Assert.Equal(first, second);
        Assert.DoesNotContain("HomeRegionC", first);
        Assert.DoesNotContain("HomeRegionW", first);
        Assert.Equal(first.Count, first.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ProbeOnlyAcceleratesSelectedDynamicAndWarReportEndpoints()
    {
        var profile = EnabledProfile(maxMaps: 1);
        var activeMaps = new[]
        {
            "DeadLandsHex",
            "MarbanHollow",
        };
        var selected = Assert.Single(
            WarApiMeasurementProbePolicy.SelectMaps(
                profile,
                "live-1",
                activeMaps));

        var dynamicEndpoint = WarApiCatalog.DynamicMap(selected);
        var reportEndpoint = WarApiCatalog.WarReport(selected);
        var staticEndpoint = WarApiCatalog.StaticMap(selected);
        var otherMap = activeMaps.Single(
            map => !string.Equals(
                map,
                selected,
                StringComparison.Ordinal));

        Assert.Equal(
            TimeSpan.FromSeconds(15),
            WarApiMeasurementProbePolicy.ResolveCadence(
                profile,
                "live-1",
                dynamicEndpoint,
                activeMaps,
                TimeSpan.FromMinutes(1)));
        Assert.Equal(
            TimeSpan.FromSeconds(15),
            WarApiMeasurementProbePolicy.ResolveCadence(
                profile,
                "live-1",
                reportEndpoint,
                activeMaps,
                TimeSpan.FromMinutes(1)));
        Assert.Equal(
            TimeSpan.FromHours(6),
            WarApiMeasurementProbePolicy.ResolveCadence(
                profile,
                "live-1",
                staticEndpoint,
                activeMaps,
                TimeSpan.FromHours(6)));
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            WarApiMeasurementProbePolicy.ResolveCadence(
                profile,
                "live-1",
                WarApiCatalog.DynamicMap(otherMap),
                activeMaps,
                TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void SelectedProbeSchedulingIdentityIsStableAndBounded()
    {
        var profile = EnabledProfile(maxMaps: 3);
        const string basePolicy =
            "warapi-poll@1/warapi-bootstrap-profile@1";

        var first = WarApiMeasurementProbePolicy.SchedulingPolicy(
            basePolicy,
            profile,
            selected: true);
        var second = WarApiMeasurementProbePolicy.SchedulingPolicy(
            basePolicy,
            profile,
            selected: true);

        Assert.Equal(first, second);
        Assert.StartsWith(
            $"{basePolicy}/m4-probe@1-",
            first,
            StringComparison.Ordinal);
        Assert.Contains("-n3-t15s", first, StringComparison.Ordinal);
        Assert.True(first.Length <= 128);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ProbeRejectsUnboundedMapCount(int maxMaps)
    {
        var profile = EnabledProfile(maxMaps);

        Assert.Throws<ArgumentOutOfRangeException>(
            profile.Validate);
    }

    [Fact]
    public void EnabledProbeRequiresRunId()
    {
        var profile = new WarApiMeasurementProbeProfile(
            Enabled: true,
            RunId: null,
            ShardKeys: ["live-1"],
            MaxMapsPerShard: 3,
            TargetCadence: TimeSpan.FromSeconds(15));

        Assert.Throws<ArgumentException>(profile.Validate);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(61)]
    public void ProbeRejectsCadenceOutsideM4Bounds(int seconds)
    {
        var profile = new WarApiMeasurementProbeProfile(
            Enabled: true,
            RunId: "m4-test",
            ShardKeys: ["live-1"],
            MaxMapsPerShard: 3,
            TargetCadence: TimeSpan.FromSeconds(seconds));

        Assert.Throws<ArgumentOutOfRangeException>(
            profile.Validate);
    }

    private static WarApiMeasurementProbeProfile EnabledProfile(
        int maxMaps) =>
        new(
            Enabled: true,
            RunId: "m4-test-run",
            ShardKeys: ["live-1"],
            MaxMapsPerShard: maxMaps,
            TargetCadence: TimeSpan.FromSeconds(15));
}
