using FoxData.Application.Canonical;
using FoxData.Sources.WarApi;

namespace FoxData.Worker;

internal static class WarApiCoverageCapabilityPlans
{
    public static IReadOnlyList<CoverageCapabilityPlan> M5Canonical { get; } =
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
    ];
}
