using FoxData.Application.Canonical;
using FoxData.Sources.WarApi;

namespace FoxData.Worker;

internal static class WarApiCoverageCapabilityPlans
{
    public static IReadOnlyList<CoverageCapabilityPlan> CoverageAndParse { get; } =
    [
        new(
            WarApiCapabilities.RuntimeWarState.Key,
            WarApiVersions.Parser,
            WarApiVersions.WarNormalizer,
            DependencyRank: 0),
        new(
            WarApiCapabilities.ActiveMapList.Key,
            WarApiVersions.Parser,
            WarApiVersions.RegionNormalizer,
            DependencyRank: 1),
        new(
            WarApiCapabilities.RegionWarReport.Key,
            WarApiVersions.Parser,
            WarApiVersions.WarReportNormalizer,
            DependencyRank: 2),
        new(
            WarApiCapabilities.StaticMapState.Key,
            WarApiVersions.Parser,
            WarApiVersions.StaticMapNormalizer,
            DependencyRank: 0),
        new(
            WarApiCapabilities.DynamicMapState.Key,
            WarApiVersions.Parser,
            WarApiVersions.DynamicMapNormalizer,
            DependencyRank: 0),
    ];

    public static IReadOnlyList<CoverageCapabilityPlan> CanonicalNormalization { get; } =
        CoverageAndParse;

    public static IReadOnlyList<CoverageCapabilityPlan> QualityRecovery { get; } =
    [
        new(
            WarApiCapabilities.StaticMapState.Key,
            WarApiVersions.Parser,
            WarApiVersions.StaticMapNormalizer,
            DependencyRank: 0),
        new(
            WarApiCapabilities.DynamicMapState.Key,
            WarApiVersions.Parser,
            WarApiVersions.DynamicMapNormalizer,
            DependencyRank: 0),
    ];
}
