using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FoxData.Sources.Abstractions;
using FoxData.Sources.WarApi;
using Microsoft.Extensions.Configuration;

namespace FoxData.Worker;

public static class WarApiCollectionPolicyConfiguration
{
    private const string BootstrapPreset = "bootstrap";
    private const string RecommendedPreset = "recommended";
    private const string CustomPreset = "custom";

    private static readonly SourceCapability[] RequiredCapabilities =
    [
        WarApiCapabilities.RuntimeWarState,
        WarApiCapabilities.ActiveMapList,
        WarApiCapabilities.RegionWarReport,
        WarApiCapabilities.StaticMapState,
        WarApiCapabilities.DynamicMapState,
    ];

    public static WarApiCollectionProfile FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration
            .GetSection("WarApi")
            .GetSection("Collection");

        var preset = section["Preset"];
        var normalizedPreset = string.IsNullOrWhiteSpace(preset)
            ? BootstrapPreset
            : preset.Trim().ToLowerInvariant();

        return normalizedPreset switch
        {
            BootstrapPreset => WarApiCollectionProfile.Bootstrap,
            RecommendedPreset => WarApiCollectionProfile.Recommended,
            CustomPreset => BuildCustom(section.GetSection("Custom")),
            _ => throw new InvalidOperationException(
                "WarApi:Collection:Preset must be one of: bootstrap, recommended, custom."),
        };
    }

    private static WarApiCollectionProfile BuildCustom(
        IConfigurationSection customSection)
    {
        var capabilities =
            new Dictionary<string, WarApiCapabilityCollectionProfile>(
                StringComparer.Ordinal);

        foreach (var capability in RequiredCapabilities)
        {
            var capabilitySection =
                customSection.GetSection(capability.Key);
            var targetSeconds = ReadRequiredPositiveInt32(
                capabilitySection,
                "TargetCadenceSeconds",
                capability.Key);
            var discoverySeconds = ReadOptionalPositiveInt32(
                capabilitySection,
                "DiscoveryWindowSeconds",
                targetSeconds,
                capability.Key);

            capabilities[capability.Key] = new(
                TimeSpan.FromSeconds(targetSeconds),
                TimeSpan.FromSeconds(discoverySeconds));
        }

        var canonical = string.Join(
            "|",
            RequiredCapabilities.Select(
                capability =>
                {
                    var value = capabilities[capability.Key];
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"{capability.Key}:{value.TargetCadence.TotalMilliseconds:0}:{value.DiscoveryWindow.TotalMilliseconds:0}");
                }));

        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical));
        var identity = Convert
            .ToHexString(hash)
            .ToLowerInvariant()[..16];

        return new WarApiCollectionProfile(
            $"custom-collection@sha256-{identity}",
            executorConcurrency: 1,
            capabilities,
            limitations:
            [
                "Operator-defined collection policy.",
                "Custom cadence may be outside the M4 measured trade-off range; the safety envelope still applies.",
            ]);
    }

    private static int ReadRequiredPositiveInt32(
        IConfigurationSection section,
        string key,
        string capabilityKey)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value) ||
            !int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed <= 0)
        {
            throw new InvalidOperationException(
                $"WarApi:Collection:Custom:{capabilityKey}:{key} must be a positive integer.");
        }

        return parsed;
    }

    private static int ReadOptionalPositiveInt32(
        IConfigurationSection section,
        string key,
        int defaultValue,
        string capabilityKey)
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
                out var parsed) ||
            parsed <= 0)
        {
            throw new InvalidOperationException(
                $"WarApi:Collection:Custom:{capabilityKey}:{key} must be a positive integer.");
        }

        return parsed;
    }
}
