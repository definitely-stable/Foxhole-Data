using FoxData.Sources.WarApi;
using FoxData.Worker;
using Microsoft.Extensions.Configuration;

namespace FoxData.IntegrationTests;

public sealed class M4ProbeConfigurationTests
{
    [Fact]
    public void ProbeIsDisabledByDefault()
    {
        var configuration = BuildConfiguration([]);
        var workerOptions =
            WarApiWorkerOptions.FromConfiguration(configuration);

        var probe =
            WarApiMeasurementProbeConfiguration.FromConfiguration(
                configuration,
                workerOptions);

        Assert.False(probe.Enabled);
        Assert.Null(probe.RunId);
        Assert.Equal(3, probe.MaxMapsPerShard);
        Assert.Equal(
            TimeSpan.FromSeconds(15),
            probe.TargetCadence);
    }

    [Fact]
    public void EnabledProbeReadsBoundedConfiguration()
    {
        var configuration = BuildConfiguration(
        [
            Pair("WarApi:Enabled", "true"),
            Pair("WarApi:MeasurementProbe:Enabled", "true"),
            Pair("WarApi:MeasurementProbe:RunId", "m4-live1-001"),
            Pair("WarApi:MeasurementProbe:EnabledShards:0", "live-1"),
            Pair("WarApi:MeasurementProbe:MaxMapsPerShard", "2"),
            Pair("WarApi:MeasurementProbe:TargetCadenceSeconds", "20"),
        ]);
        var workerOptions =
            WarApiWorkerOptions.FromConfiguration(configuration);

        var probe =
            WarApiMeasurementProbeConfiguration.FromConfiguration(
                configuration,
                workerOptions);

        Assert.True(probe.Enabled);
        Assert.Equal("m4-live1-001", probe.RunId);
        Assert.Equal(["live-1"], probe.ShardKeys);
        Assert.Equal(2, probe.MaxMapsPerShard);
        Assert.Equal(
            TimeSpan.FromSeconds(20),
            probe.TargetCadence);
    }

    [Fact]
    public void EnabledProbeRejectsDevShard()
    {
        var configuration = BuildConfiguration(
        [
            Pair("WarApi:EnableDev", "true"),
            Pair("WarApi:EnabledShards:0", "dev"),
            Pair("WarApi:MeasurementProbe:Enabled", "true"),
            Pair("WarApi:MeasurementProbe:RunId", "m4-dev"),
            Pair("WarApi:MeasurementProbe:EnabledShards:0", "dev"),
        ]);
        var workerOptions =
            WarApiWorkerOptions.FromConfiguration(configuration);

        var exception = Assert.Throws<InvalidOperationException>(
            () =>
                WarApiMeasurementProbeConfiguration.FromConfiguration(
                    configuration,
                    workerOptions));

        Assert.Contains(
            "Dev shard",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledProbeRequiresStableRunIdentifier()
    {
        var configuration = BuildConfiguration(
        [
            Pair("WarApi:MeasurementProbe:Enabled", "true"),
            Pair("WarApi:MeasurementProbe:EnabledShards:0", "live-1"),
        ]);
        var workerOptions =
            WarApiWorkerOptions.FromConfiguration(configuration);

        Assert.Throws<ArgumentException>(
            () =>
                WarApiMeasurementProbeConfiguration.FromConfiguration(
                    configuration,
                    workerOptions));
    }

    [Fact]
    public void ProbeShardMustAlsoBeEnabledForWorker()
    {
        var configuration = BuildConfiguration(
        [
            Pair("WarApi:EnabledShards:0", "live-1"),
            Pair("WarApi:MeasurementProbe:Enabled", "true"),
            Pair("WarApi:MeasurementProbe:RunId", "m4-live2"),
            Pair("WarApi:MeasurementProbe:EnabledShards:0", "live-2"),
        ]);
        var workerOptions =
            WarApiWorkerOptions.FromConfiguration(configuration);

        var exception = Assert.Throws<InvalidOperationException>(
            () =>
                WarApiMeasurementProbeConfiguration.FromConfiguration(
                    configuration,
                    workerOptions));

        Assert.Contains(
            "must also be enabled",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static IConfiguration BuildConfiguration(
        IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private static KeyValuePair<string, string?> Pair(
        string key,
        string value) =>
        new(key, value);
}
