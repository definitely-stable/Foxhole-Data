using FoxData.Sources.Abstractions;

namespace FoxData.Sources.WarApi;

public sealed record WarApiCapabilityCollectionProfile(
    TimeSpan TargetCadence,
    TimeSpan DiscoveryWindow);

public sealed class WarApiCollectionProfile
{
    public const string BootstrapVersion = "warapi-bootstrap-profile@1";

    private static readonly string[] RequiredCapabilityKeys =
    [
        "runtime-war-state",
        "active-map-list",
        "region-war-report",
        "static-map-state",
        "dynamic-map-state",
    ];

    private readonly IReadOnlyDictionary<string, WarApiCapabilityCollectionProfile> _capabilities;

    public WarApiCollectionProfile(
        string version,
        int executorConcurrency,
        IReadOnlyDictionary<string, WarApiCapabilityCollectionProfile> capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(capabilities);

        if (!string.Equals(version, version.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Collection profile version must already be trimmed.",
                nameof(version));
        }

        if (executorConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(executorConcurrency),
                executorConcurrency,
                "Executor concurrency must be positive.");
        }

        var copy = new Dictionary<string, WarApiCapabilityCollectionProfile>(
            capabilities,
            StringComparer.Ordinal);

        foreach (var key in RequiredCapabilityKeys)
        {
            if (!copy.TryGetValue(key, out var capabilityProfile))
            {
                throw new ArgumentException(
                    $"Collection profile is missing required capability '{key}'.",
                    nameof(capabilities));
            }

            if (capabilityProfile.TargetCadence <= TimeSpan.Zero)
            {
                throw new ArgumentException(
                    $"Capability '{key}' has a non-positive target cadence.",
                    nameof(capabilities));
            }

            if (capabilityProfile.DiscoveryWindow <= TimeSpan.Zero)
            {
                throw new ArgumentException(
                    $"Capability '{key}' has a non-positive discovery window.",
                    nameof(capabilities));
            }
        }

        var unknown = copy.Keys
            .Except(RequiredCapabilityKeys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (unknown.Length != 0)
        {
            throw new ArgumentException(
                $"Collection profile contains unsupported capabilities: {string.Join(", ", unknown)}.",
                nameof(capabilities));
        }

        Version = version;
        ExecutorConcurrency = executorConcurrency;
        _capabilities = copy;
    }

    public string Version { get; }

    public int ExecutorConcurrency { get; }

    public static WarApiCollectionProfile Bootstrap { get; } =
        new(
            BootstrapVersion,
            executorConcurrency: 1,
            new Dictionary<string, WarApiCapabilityCollectionProfile>(
                StringComparer.Ordinal)
            {
                ["runtime-war-state"] = new(
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(1)),
                ["active-map-list"] = new(
                    TimeSpan.FromMinutes(5),
                    TimeSpan.FromMinutes(5)),
                ["region-war-report"] = new(
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(1)),
                ["static-map-state"] = new(
                    TimeSpan.FromHours(6),
                    TimeSpan.FromMinutes(5)),
                ["dynamic-map-state"] = new(
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(1)),
            });

    public WarApiCapabilityCollectionProfile Get(SourceCapability capability)
    {
        if (!_capabilities.TryGetValue(capability.Key, out var profile))
        {
            throw new ArgumentException(
                $"Unsupported War API capability '{capability.Key}'.",
                nameof(capability));
        }

        return profile;
    }

    public TimeSpan TargetCadence(SourceCapability capability) =>
        Get(capability).TargetCadence;

    public TimeSpan DiscoveryWindow(SourceCapability capability) =>
        Get(capability).DiscoveryWindow;
}
