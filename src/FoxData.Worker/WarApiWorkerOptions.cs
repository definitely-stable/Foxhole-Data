using System.Globalization;
using FoxData.Sources.WarApi;
using Microsoft.Extensions.Configuration;

namespace FoxData.Worker;

public sealed record WarApiWorkerOptions(
    bool Enabled,
    IReadOnlyList<WarApiShard> Shards,
    TimeSpan LeaseDuration,
    TimeSpan IdleDelay,
    TimeSpan RecoveryInterval,
    TimeSpan PlannerInterval,
    int PlannerBatchSize,
    TimeSpan ConnectTimeout,
    TimeSpan ExchangeTimeout,
    int MaxConnectionsPerServer,
    int MaxResponseHeadersLengthKiB,
    int MaxWireBytes,
    int MaxDecodedBytes,
    double MaxExpansionRatio,
    TimeSpan OutboundGlobalMinimumInterval,
    TimeSpan OutboundPerHostMinimumInterval,
    string UserAgent)
{
    public static WarApiWorkerOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("WarApi");
        var enabled = ReadBoolean(section, "Enabled", false);
        var enableDev = ReadBoolean(section, "EnableDev", false);

        var configuredShards = section
            .GetSection("EnabledShards")
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => WarApiCatalog.ParseShardKey(value!))
            .ToList();

        if (configuredShards.Count == 0)
        {
            configuredShards.Add(WarApiShard.Live1);
        }

        if (enableDev && !configuredShards.Contains(WarApiShard.Dev))
        {
            configuredShards.Add(WarApiShard.Dev);
        }

        if (!enableDev && configuredShards.Contains(WarApiShard.Dev))
        {
            throw new InvalidOperationException(
                "WarApi:EnableDev must be true before the Dev shard can be enabled.");
        }

        var distinctShards = configuredShards
            .Distinct()
            .ToArray();

        var options = new WarApiWorkerOptions(
            enabled,
            distinctShards,
            TimeSpan.FromSeconds(ReadInt32(section, "LeaseDurationSeconds", 120)),
            TimeSpan.FromMilliseconds(ReadInt32(section, "IdleDelayMilliseconds", 500)),
            TimeSpan.FromSeconds(ReadInt32(section, "RecoveryIntervalSeconds", 5)),
            TimeSpan.FromSeconds(ReadInt32(section, "PlannerIntervalSeconds", 2)),
            ReadInt32(section, "PlannerBatchSize", 64),
            TimeSpan.FromSeconds(ReadInt32(section, "Http:ConnectTimeoutSeconds", 10)),
            TimeSpan.FromSeconds(ReadInt32(section, "Http:ExchangeTimeoutSeconds", 30)),
            ReadInt32(section, "Http:MaxConnectionsPerServer", 8),
            ReadInt32(section, "Http:MaxResponseHeadersKiB", 32),
            ReadInt32(section, "Http:MaxWireBytes", 8 * 1024 * 1024),
            ReadInt32(section, "Http:MaxDecodedBytes", 16 * 1024 * 1024),
            ReadDouble(section, "Http:MaxExpansionRatio", 20),
            TimeSpan.FromMilliseconds(
                ReadInt32(section, "Http:OutboundGlobalMinimumIntervalMilliseconds", 150)),
            TimeSpan.FromMilliseconds(
                ReadInt32(section, "Http:OutboundPerHostMinimumIntervalMilliseconds", 400)),
            ReadString(
                section,
                "Http:UserAgent",
                "Foxhole-Chronicle/FoxData-M4 (+https://github.com/definitely-stable/Foxhole-Data)"));

        Validate(options, configuration);
        return options;
    }

    private static void Validate(
        WarApiWorkerOptions options,
        IConfiguration configuration)
    {
        if (options.Shards.Count == 0)
        {
            throw new InvalidOperationException("At least one War API shard must be configured.");
        }

        if (options.LeaseDuration <= TimeSpan.Zero ||
            options.IdleDelay <= TimeSpan.Zero ||
            options.RecoveryInterval <= TimeSpan.Zero ||
            options.PlannerInterval <= TimeSpan.Zero ||
            options.ConnectTimeout <= TimeSpan.Zero ||
            options.ExchangeTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "War API durations must be positive.");
        }

        if (options.PlannerBatchSize is < 1 or > 4096)
        {
            throw new InvalidOperationException(
                "WarApi:PlannerBatchSize must be between 1 and 4096.");
        }

        if (options.MaxConnectionsPerServer < 1 ||
            options.MaxResponseHeadersLengthKiB < 1 ||
            options.MaxWireBytes < 1 ||
            options.MaxDecodedBytes < 1)
        {
            throw new InvalidOperationException(
                "War API HTTP limits must be positive.");
        }

        if (!double.IsFinite(options.MaxExpansionRatio) ||
            options.MaxExpansionRatio < 1)
        {
            throw new InvalidOperationException(
                "WarApi:Http:MaxExpansionRatio must be finite and at least 1.");
        }

        if (options.MaxDecodedBytes < options.MaxWireBytes)
        {
            throw new InvalidOperationException(
                "WarApi:Http:MaxDecodedBytes must be at least MaxWireBytes.");
        }

        if (options.OutboundGlobalMinimumInterval < TimeSpan.FromMilliseconds(150))
        {
            throw new InvalidOperationException(
                "WarApi:Http:OutboundGlobalMinimumIntervalMilliseconds must be at least 150 ms.");
        }

        if (options.OutboundPerHostMinimumInterval < TimeSpan.FromMilliseconds(400))
        {
            throw new InvalidOperationException(
                "WarApi:Http:OutboundPerHostMinimumIntervalMilliseconds must be at least 400 ms.");
        }

        if (string.IsNullOrWhiteSpace(options.UserAgent) ||
            !string.Equals(options.UserAgent, options.UserAgent.Trim(), StringComparison.Ordinal) ||
            options.UserAgent.Contains('\r', StringComparison.Ordinal) ||
            options.UserAgent.Contains('\n', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "WarApi:Http:UserAgent must be a non-empty single-line value.");
        }

        var evidenceLimit = ReadInt64(
            configuration.GetSection("EvidenceKernel"),
            "MaxPayloadBytes",
            64L * 1024 * 1024);

        if (options.MaxWireBytes > evidenceLimit)
        {
            throw new InvalidOperationException(
                "WarApi:Http:MaxWireBytes must not exceed EvidenceKernel:MaxPayloadBytes.");
        }
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
                $"Configuration value 'WarApi:{key}' must be a boolean.");
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
                $"Configuration value 'WarApi:{key}' must be an integer.");
        }

        return parsed;
    }

    private static long ReadInt64(
        IConfiguration section,
        string key,
        long defaultValue)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value 'WarApi:{key}' must be a 64-bit integer.");
        }

        return parsed;
    }

    private static double ReadDouble(
        IConfiguration section,
        string key,
        double defaultValue)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value 'WarApi:{key}' must be a number.");
        }

        return parsed;
    }

    private static string ReadString(
        IConfiguration section,
        string key,
        string defaultValue)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : value;
    }
}
