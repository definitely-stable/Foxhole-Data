using FoxData.Sources.WarApi;
using FoxData.Worker;
using Microsoft.Extensions.Configuration;

namespace FoxData.RecoveryTests;

public sealed class WarApiCollectionPolicyConfigurationTests
{
    [Fact]
    public void MissingPresetPreservesBootstrapBehavior()
    {
        var configuration = new ConfigurationManager();

        var profile =
            WarApiCollectionPolicyConfiguration.FromConfiguration(
                configuration);

        Assert.Same(WarApiCollectionProfile.Bootstrap, profile);
    }

    [Fact]
    public void RecommendedPresetIsExplicitOptIn()
    {
        var configuration = new ConfigurationManager
        {
            ["WarApi:Collection:Preset"] = "recommended",
        };

        var profile =
            WarApiCollectionPolicyConfiguration.FromConfiguration(
                configuration);

        Assert.Same(WarApiCollectionProfile.Recommended, profile);
    }

    [Fact]
    public void CustomPolicyUsesConfiguredCadenceAndStableIdentity()
    {
        var first = CustomConfiguration();
        var second = CustomConfiguration();

        var firstProfile =
            WarApiCollectionPolicyConfiguration.FromConfiguration(first);
        var secondProfile =
            WarApiCollectionPolicyConfiguration.FromConfiguration(second);

        Assert.Equal(firstProfile.Version, secondProfile.Version);
        Assert.StartsWith(
            "custom-collection@sha256-",
            firstProfile.Version,
            StringComparison.Ordinal);

        Assert.Equal(
            TimeSpan.FromSeconds(45),
            firstProfile.TargetCadence(
                WarApiCapabilities.DynamicMapState));
        Assert.Equal(
            TimeSpan.FromSeconds(90),
            firstProfile.DiscoveryWindow(
                WarApiCapabilities.DynamicMapState));
    }

    [Fact]
    public void CustomPolicyIdentityChangesWithCadence()
    {
        var first = CustomConfiguration();
        var second = CustomConfiguration();
        second[
            "WarApi:Collection:Custom:dynamic-map-state:TargetCadenceSeconds"] =
            "46";

        var firstProfile =
            WarApiCollectionPolicyConfiguration.FromConfiguration(first);
        var secondProfile =
            WarApiCollectionPolicyConfiguration.FromConfiguration(second);

        Assert.NotEqual(firstProfile.Version, secondProfile.Version);
    }

    [Fact]
    public void CustomPolicyRequiresEveryTargetCadence()
    {
        var configuration = CustomConfiguration();
        configuration[
            "WarApi:Collection:Custom:region-war-report:TargetCadenceSeconds"] =
            null;

        var exception = Assert.Throws<InvalidOperationException>(
            () =>
                WarApiCollectionPolicyConfiguration.FromConfiguration(
                    configuration));

        Assert.Contains(
            "region-war-report",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static ConfigurationManager CustomConfiguration()
    {
        var configuration = new ConfigurationManager
        {
            ["WarApi:Collection:Preset"] = "custom",
        };

        SetCapability(
            configuration,
            "runtime-war-state",
            targetSeconds: 60,
            discoverySeconds: 60);
        SetCapability(
            configuration,
            "active-map-list",
            targetSeconds: 300,
            discoverySeconds: 300);
        SetCapability(
            configuration,
            "region-war-report",
            targetSeconds: 30,
            discoverySeconds: 30);
        SetCapability(
            configuration,
            "static-map-state",
            targetSeconds: 21600,
            discoverySeconds: 300);
        SetCapability(
            configuration,
            "dynamic-map-state",
            targetSeconds: 45,
            discoverySeconds: 90);

        return configuration;
    }

    private static void SetCapability(
        ConfigurationManager configuration,
        string capability,
        int targetSeconds,
        int discoverySeconds)
    {
        configuration[
            $"WarApi:Collection:Custom:{capability}:TargetCadenceSeconds"] =
            targetSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        configuration[
            $"WarApi:Collection:Custom:{capability}:DiscoveryWindowSeconds"] =
            discoverySeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
    }
}
