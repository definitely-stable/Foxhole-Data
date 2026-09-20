using FoxData.Sources.Abstractions;

namespace FoxData.Sources.WarApi;

public enum WarApiShard
{
    Live1,
    Live2,
    Live3,
    Dev,
}

public static class WarApiCapabilities
{
    public static readonly SourceCapability RuntimeWarState = new("runtime-war-state");
    public static readonly SourceCapability ActiveMapList = new("active-map-list");
    public static readonly SourceCapability RegionWarReport = new("region-war-report");
    public static readonly SourceCapability StaticMapState = new("static-map-state");
    public static readonly SourceCapability DynamicMapState = new("dynamic-map-state");
}

public static class WarApiCatalog
{
    public const string SourceKey = "official-war-api";
    public const int MaximumMapNameLength = 128;

    public static string GetEnvironment(WarApiShard shard) =>
        shard == WarApiShard.Dev ? "dev" : "live";

    public static string GetShardKey(WarApiShard shard) =>
        shard switch
        {
            WarApiShard.Live1 => "live-1",
            WarApiShard.Live2 => "live-2",
            WarApiShard.Live3 => "live-3",
            WarApiShard.Dev => "dev",
            _ => throw new ArgumentOutOfRangeException(nameof(shard), shard, "Unknown War API shard."),
        };

    public static WarApiShard ParseShardKey(string key) =>
        key switch
        {
            "live-1" => WarApiShard.Live1,
            "live-2" => WarApiShard.Live2,
            "live-3" => WarApiShard.Live3,
            "dev" => WarApiShard.Dev,
            _ => throw new ArgumentException($"Unsupported War API shard key '{key}'.", nameof(key)),
        };

    public static Uri GetRoot(WarApiShard shard) =>
        shard switch
        {
            WarApiShard.Live1 => new Uri("https://war-service-live.foxholeservices.com/api/", UriKind.Absolute),
            WarApiShard.Live2 => new Uri("https://war-service-live-2.foxholeservices.com/api/", UriKind.Absolute),
            WarApiShard.Live3 => new Uri("https://war-service-live-3.foxholeservices.com/api/", UriKind.Absolute),
            WarApiShard.Dev => new Uri("https://war-service-dev.foxholeservices.com/api/", UriKind.Absolute),
            _ => throw new ArgumentOutOfRangeException(nameof(shard), shard, "Unknown War API shard."),
        };

    public static SourceEndpoint War() =>
        new(WarApiCapabilities.RuntimeWarState, "war");

    public static SourceEndpoint Maps() =>
        new(WarApiCapabilities.ActiveMapList, "maps");

    public static SourceEndpoint WarReport(string mapName)
    {
        var validated = ValidateMapName(mapName);
        return new SourceEndpoint(
            WarApiCapabilities.RegionWarReport,
            $"war-report/{validated}",
            validated);
    }

    public static SourceEndpoint StaticMap(string mapName)
    {
        var validated = ValidateMapName(mapName);
        return new SourceEndpoint(
            WarApiCapabilities.StaticMapState,
            $"map-static/{validated}",
            validated);
    }

    public static SourceEndpoint DynamicMap(string mapName)
    {
        var validated = ValidateMapName(mapName);
        return new SourceEndpoint(
            WarApiCapabilities.DynamicMapState,
            $"map-dynamic/{validated}",
            validated);
    }

    public static SourceEndpoint FromRegistry(
        string capabilityKey,
        string semanticKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticKey);

        return capabilityKey switch
        {
            "runtime-war-state" when semanticKey == "war" => War(),
            "active-map-list" when semanticKey == "maps" => Maps(),
            "region-war-report" => FromMapSemanticKey(
                semanticKey,
                "war-report/",
                WarReport),
            "static-map-state" => FromMapSemanticKey(
                semanticKey,
                "map-static/",
                StaticMap),
            "dynamic-map-state" => FromMapSemanticKey(
                semanticKey,
                "map-dynamic/",
                DynamicMap),
            _ => throw new ArgumentException(
                $"Registry endpoint '{capabilityKey}' / '{semanticKey}' is not a valid War API endpoint."),
        };
    }

    public static string ValidateMapName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length > MaximumMapNameLength)
        {
            throw new ArgumentException(
                $"Map name must be at most {MaximumMapNameLength} characters.",
                nameof(value));
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Map name must already be trimmed.", nameof(value));
        }

        foreach (var character in value)
        {
            if (character is '/' or '\\' || char.IsControl(character))
            {
                throw new ArgumentException(
                    "Map name must be one opaque path segment.",
                    nameof(value));
            }
        }

        return value;
    }

    public static Uri BuildUri(WarApiShard shard, SourceEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var relativePath = endpoint.Capability.Key switch
        {
            "runtime-war-state" => "worldconquest/war",
            "active-map-list" => "worldconquest/maps",
            "region-war-report" => $"worldconquest/warReport/{EscapeIdentifier(endpoint)}",
            "static-map-state" => $"worldconquest/maps/{EscapeIdentifier(endpoint)}/static",
            "dynamic-map-state" => $"worldconquest/maps/{EscapeIdentifier(endpoint)}/dynamic/public",
            _ => throw new ArgumentException(
                $"Unsupported War API capability '{endpoint.Capability.Key}'.",
                nameof(endpoint)),
        };

        return new Uri(GetRoot(shard), relativePath);
    }

    private static SourceEndpoint FromMapSemanticKey(
        string semanticKey,
        string prefix,
        Func<string, SourceEndpoint> factory)
    {
        if (!semanticKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Semantic key '{semanticKey}' does not match prefix '{prefix}'.",
                nameof(semanticKey));
        }

        var mapName = semanticKey[prefix.Length..];
        return factory(ValidateMapName(mapName));
    }

    private static string EscapeIdentifier(SourceEndpoint endpoint)
    {
        var identifier = ValidateMapName(
            endpoint.SourceIdentifier
            ?? throw new ArgumentException(
                "Map-scoped endpoint requires a source identifier.",
                nameof(endpoint)));

        return Uri.EscapeDataString(identifier);
    }
}
