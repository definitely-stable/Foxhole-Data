using FoxData.Sources.Abstractions;

namespace FoxData.Sources.WarApi;

public sealed record WarApiCapabilityCollectionProfile(
    TimeSpan TargetCadence,
    TimeSpan DiscoveryWindow);

public sealed class WarApiCollectionProfile
{
    public const string BootstrapVersion = "warapi-bootstrap-profile@1";
    public const string RecommendedVersion = "collection-profile@1";

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
        IReadOnlyDictionary<string, WarApiCapabilityCollectionProfile> capabilities,
        string? measurementReference = null,
        IReadOnlyList<string>? limitations = null)
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

        if (measurementReference is not null &&
            (string.IsNullOrWhiteSpace(measurementReference) ||
             !string.Equals(
                 measurementReference,
                 measurementReference.Trim(),
                 StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Measurement reference must be non-empty and already trimmed when supplied.",
                nameof(measurementReference));
        }

        var limitationCopy =
            (limitations ?? Array.Empty<string>())
            .ToArray();

        if (limitationCopy.Any(
                limitation =>
                    string.IsNullOrWhiteSpace(limitation) ||
                    !string.Equals(
                        limitation,
                        limitation.Trim(),
                        StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Collection profile limitations must be non-empty and already trimmed.",
                nameof(limitations));
        }

        if (limitationCopy.Distinct(
                StringComparer.Ordinal).Count() !=
            limitationCopy.Length)
        {
            throw new ArgumentException(
                "Collection profile limitations must be unique.",
                nameof(limitations));
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
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        if (unknown.Length != 0)
        {
            throw new ArgumentException(
                $"Collection profile contains unsupported capabilities: {string.Join(", ", unknown)}.",
                nameof(capabilities));
        }

        Version = version;
        ExecutorConcurrency = executorConcurrency;
        MeasurementReference = measurementReference;
        Limitations = limitationCopy;
        _capabilities = copy;
    }

    public string Version { get; }

    public int ExecutorConcurrency { get; }

    public string? MeasurementReference { get; }

    public IReadOnlyList<string> Limitations { get; }

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

    public static WarApiCollectionProfile Recommended { get; } =
        new(
            RecommendedVersion,
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
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(30)),
                ["static-map-state"] = new(
                    TimeSpan.FromHours(6),
                    TimeSpan.FromMinutes(5)),
                ["dynamic-map-state"] = new(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(30)),
            },
            measurementReference: "m4:m4-ci-35604611891",
            limitations:
            [
                "48.169 active measurement hours from the September 2026 M4 campaign.",
                "15/30/60/120 second downsampling is based on an 8-hour deterministic three-region Live-1 probe.",
                "The 30-second dynamic/report target is a balanced freshness/load recommendation, not a completeness guarantee.",
                "Live-2 and Live-3 root endpoints returned 503 during their observed campaign phases.",
            ]);

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
