using FoxData.Sources.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace FoxData.Sources.WarApi;

public sealed record WarApiMeasurementProbeProfile(
    bool Enabled,
    string? RunId,
    int MaxMapsPerShard,
    TimeSpan TargetCadence)
{
    public const string Version = "m4-probe@1";

    public static WarApiMeasurementProbeProfile Disabled { get; } =
        new(
            Enabled: false,
            RunId: null,
            MaxMapsPerShard: 3,
            TargetCadence: TimeSpan.FromSeconds(15));

    public void Validate()
    {
        if (MaxMapsPerShard is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxMapsPerShard),
                MaxMapsPerShard,
                "M4 probe map count must be between 1 and 3.");
        }

        if (TargetCadence < TimeSpan.FromSeconds(15) ||
            TargetCadence > TimeSpan.FromSeconds(60) ||
            TargetCadence.TotalSeconds !=
                Math.Truncate(TargetCadence.TotalSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(TargetCadence),
                TargetCadence,
                "M4 probe cadence must be a whole number of seconds between 15 and 60.");
        }

        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(RunId) ||
            RunId.Length > 64 ||
            !string.Equals(
                RunId,
                RunId.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Enabled M4 probe requires a non-empty, already-trimmed RunId of at most 64 characters.",
                nameof(RunId));
        }
    }
}

public static class WarApiMeasurementProbePolicy
{
    public static IReadOnlyList<string> SelectMaps(
        WarApiMeasurementProbeProfile profile,
        string shardKey,
        IEnumerable<string> activeMapNames)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(shardKey);
        ArgumentNullException.ThrowIfNull(activeMapNames);

        profile.Validate();

        if (!profile.Enabled)
        {
            return [];
        }

        return activeMapNames
            .Where(
                mapName =>
                    !string.IsNullOrWhiteSpace(mapName) &&
                    mapName is not ("HomeRegionC" or "HomeRegionW"))
            .Distinct(StringComparer.Ordinal)
            .Select(
                mapName =>
                    new RankedMap(
                        mapName,
                        StableRank(
                            profile.RunId!,
                            shardKey,
                            mapName)))
            .OrderBy(candidate => candidate.Rank, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.MapName, StringComparer.Ordinal)
            .Take(profile.MaxMapsPerShard)
            .Select(candidate => candidate.MapName)
            .ToArray();
    }

    public static bool IsSelectedEndpoint(
        WarApiMeasurementProbeProfile profile,
        string shardKey,
        SourceEndpoint endpoint,
        IEnumerable<string> activeMapNames)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!profile.Enabled ||
            endpoint.SourceIdentifier is null ||
            endpoint.Capability != WarApiCapabilities.DynamicMapState &&
            endpoint.Capability != WarApiCapabilities.RegionWarReport)
        {
            return false;
        }

        return SelectMaps(
                profile,
                shardKey,
                activeMapNames)
            .Contains(
                endpoint.SourceIdentifier,
                StringComparer.Ordinal);
    }

    public static TimeSpan ResolveCadence(
        WarApiMeasurementProbeProfile profile,
        string shardKey,
        SourceEndpoint endpoint,
        IEnumerable<string> activeMapNames,
        TimeSpan baseCadence)
    {
        if (baseCadence <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseCadence),
                baseCadence,
                "Base cadence must be positive.");
        }

        return IsSelectedEndpoint(
                profile,
                shardKey,
                endpoint,
                activeMapNames)
            ? Min(baseCadence, profile.TargetCadence)
            : baseCadence;
    }

    public static string SchedulingPolicy(
        string basePolicy,
        WarApiMeasurementProbeProfile profile,
        bool selected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePolicy);
        ArgumentNullException.ThrowIfNull(profile);

        profile.Validate();

        if (!profile.Enabled || !selected)
        {
            return basePolicy;
        }

        var runToken = StableRank(
            profile.RunId!,
            "policy",
            "identity")[..12];
        var seconds = checked((int)profile.TargetCadence.TotalSeconds);

        return $"{basePolicy}/{WarApiMeasurementProbeProfile.Version}-{runToken}-n{profile.MaxMapsPerShard}-t{seconds}s";
    }

    private static string StableRank(
        string runId,
        string shardKey,
        string mapName)
    {
        var input = Encoding.UTF8.GetBytes(
            $"{runId}\n{shardKey}\n{mapName}");
        return Convert.ToHexString(
                SHA256.HashData(input))
            .ToLowerInvariant();
    }

    private static TimeSpan Min(
        TimeSpan first,
        TimeSpan second) =>
        first < second ? first : second;

    private sealed record RankedMap(
        string MapName,
        string Rank);
}
