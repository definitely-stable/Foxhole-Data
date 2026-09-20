using System.Globalization;
using FoxData.Sources.WarApi;
using Microsoft.Extensions.Configuration;

namespace FoxData.Worker;

public static class WarApiMeasurementProbeConfiguration
{
    public static WarApiMeasurementProbeProfile FromConfiguration(
        IConfiguration configuration,
        WarApiWorkerOptions workerOptions)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(workerOptions);

        var section = configuration
            .GetSection("WarApi")
            .GetSection("MeasurementProbe");

        var enabled = ReadBoolean(
            section,
            "Enabled",
            defaultValue: false);
        var runId = section["RunId"];
        var shardKeys = section
            .GetSection("EnabledShards")
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
        var maxMaps = ReadInt32(
            section,
            "MaxMapsPerShard",
            3);
        var targetCadence = TimeSpan.FromSeconds(
            ReadInt32(
                section,
                "TargetCadenceSeconds",
                15));

        var profile = new WarApiMeasurementProbeProfile(
            enabled,
            runId,
            shardKeys,
            maxMaps,
            targetCadence);

        try
        {
            profile.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"Invalid WarApi:MeasurementProbe configuration: {exception.Message}",
                exception);
        }

        if (enabled)
        {
            foreach (var shardKey in shardKeys)
            {
                var shard = WarApiCatalog.ParseShardKey(shardKey);
                if (shard == WarApiShard.Dev)
                {
                    throw new InvalidOperationException(
                        "WarApi:MeasurementProbe cannot run against the Dev shard.");
                }

                if (!workerOptions.Shards.Contains(shard))
                {
                    throw new InvalidOperationException(
                        $"Probe shard '{shardKey}' must also be enabled in WarApi:EnabledShards.");
                }
            }
        }

        return profile;
    }

    private static bool ReadBoolean(
        IConfiguration section,
        string key,
        bool defaultValue)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!bool.TryParse(value, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value 'WarApi:MeasurementProbe:{key}' must be a boolean.");
        }

        return parsed;
    }

    private static int ReadInt32(
        IConfiguration section,
        string key,
        int defaultValue)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value 'WarApi:MeasurementProbe:{key}' must be an integer.");
        }

        return parsed;
    }
}
