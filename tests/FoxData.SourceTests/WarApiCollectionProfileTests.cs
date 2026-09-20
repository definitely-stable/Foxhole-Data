using System.Net;
using FoxData.Sources.Abstractions;
using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiCollectionProfileTests
{
    [Fact]
    public void BootstrapProfilePreservesM3CadenceAndConcurrency()
    {
        var profile = WarApiCollectionProfile.Bootstrap;

        Assert.Equal(WarApiCollectionProfile.BootstrapVersion, profile.Version);
        Assert.Equal(1, profile.ExecutorConcurrency);

        AssertCapability(
            profile,
            WarApiCapabilities.RuntimeWarState,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        AssertCapability(
            profile,
            WarApiCapabilities.ActiveMapList,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5));
        AssertCapability(
            profile,
            WarApiCapabilities.RegionWarReport,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        AssertCapability(
            profile,
            WarApiCapabilities.StaticMapState,
            TimeSpan.FromHours(6),
            TimeSpan.FromMinutes(5));
        AssertCapability(
            profile,
            WarApiCapabilities.DynamicMapState,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void SchedulingPolicyIdentityIncludesCollectionProfile()
    {
        Assert.Equal(
            "warapi-poll@1/warapi-bootstrap-profile@1",
            WarApiVersions.SchedulingPolicy(
                WarApiCollectionProfile.Bootstrap));
    }

    [Fact]
    public void FasterMeasuredTargetCannotBypassSourceFreshness()
    {
        var profiles = FullProfile();
        profiles[WarApiCapabilities.DynamicMapState.Key] = new(
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(15));
        var profile = new WarApiCollectionProfile(
            "collection-profile@test",
            1,
            profiles);
        var retrievedAt = new DateTimeOffset(
            2026,
            9,
            20,
            12,
            0,
            0,
            TimeSpan.Zero);

        var decision = new WarApiCachePolicy().Evaluate(
            new WarApiCacheMetadata(
                HttpStatusCode.OK,
                "max-age=60",
                null,
                retrievedAt,
                0,
                null),
            retrievedAt,
            profile.TargetCadence(
                WarApiCapabilities.DynamicMapState));

        Assert.Equal(
            retrievedAt.AddMinutes(1),
            decision.NextEligibleAt);
    }

    [Fact]
    public void FasterMeasuredTargetCannotBypassRetryAfter()
    {
        var profiles = FullProfile();
        profiles[WarApiCapabilities.DynamicMapState.Key] = new(
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(15));
        var profile = new WarApiCollectionProfile(
            "collection-profile@test",
            1,
            profiles);
        var retrievedAt = new DateTimeOffset(
            2026,
            9,
            20,
            12,
            0,
            0,
            TimeSpan.Zero);

        var decision = new WarApiCachePolicy().Evaluate(
            new WarApiCacheMetadata(
                HttpStatusCode.TooManyRequests,
                null,
                null,
                null,
                null,
                "120"),
            retrievedAt,
            profile.TargetCadence(
                WarApiCapabilities.DynamicMapState));

        Assert.Equal(
            retrievedAt.AddMinutes(2),
            decision.NextEligibleAt);
    }

    [Fact]
    public void ProfileRequiresEveryWarApiCapability()
    {
        var profiles = FullProfile();
        profiles.Remove(WarApiCapabilities.DynamicMapState.Key);

        var exception = Assert.Throws<ArgumentException>(
            () => new WarApiCollectionProfile(
                "test-profile@1",
                1,
                profiles));

        Assert.Contains(
            WarApiCapabilities.DynamicMapState.Key,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileRejectsUnknownCapability()
    {
        var profiles = FullProfile();
        profiles["future-capability"] = new(
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));

        var exception = Assert.Throws<ArgumentException>(
            () => new WarApiCollectionProfile(
                "test-profile@1",
                1,
                profiles));

        Assert.Contains(
            "future-capability",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileRejectsNonPositiveCadence()
    {
        var profiles = FullProfile();
        profiles[WarApiCapabilities.RuntimeWarState.Key] = new(
            TimeSpan.Zero,
            TimeSpan.FromMinutes(1));

        Assert.Throws<ArgumentException>(
            () => new WarApiCollectionProfile(
                "test-profile@1",
                1,
                profiles));
    }

    [Fact]
    public void UnknownCapabilityCannotBeRead()
    {
        Assert.Throws<ArgumentException>(
            () => WarApiCollectionProfile.Bootstrap.Get(
                new SourceCapability("unknown")));
    }

    private static Dictionary<string, WarApiCapabilityCollectionProfile> FullProfile() =>
        new(StringComparer.Ordinal)
        {
            [WarApiCapabilities.RuntimeWarState.Key] = new(
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1)),
            [WarApiCapabilities.ActiveMapList.Key] = new(
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5)),
            [WarApiCapabilities.RegionWarReport.Key] = new(
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1)),
            [WarApiCapabilities.StaticMapState.Key] = new(
                TimeSpan.FromHours(6),
                TimeSpan.FromMinutes(5)),
            [WarApiCapabilities.DynamicMapState.Key] = new(
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1)),
        };

    private static void AssertCapability(
        WarApiCollectionProfile profile,
        SourceCapability capability,
        TimeSpan cadence,
        TimeSpan discoveryWindow)
    {
        var actual = profile.Get(capability);
        Assert.Equal(cadence, actual.TargetCadence);
        Assert.Equal(discoveryWindow, actual.DiscoveryWindow);
    }
}
