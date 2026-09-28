using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Sources.WarApi;

namespace FoxData.Worker;

public enum WarApiDynamicMapNormalizationStatus
{
    Normalized,
    Rejected,
}

public sealed record WarApiDynamicMapNormalizationResult(
    WarApiDynamicMapNormalizationStatus Status,
    NormalizationRunDescriptor NormalizationRun,
    MapSnapshotResult? Snapshot);

public sealed class WarApiDynamicMapNormalizationCoordinator
{
    private static readonly WarApiMapSnapshotNormalizationProfile Profile =
        new(
            WarApiCapabilities.DynamicMapState,
            "map-dynamic/",
            WarApiVersions.DynamicMapNormalizer,
            MapSnapshotKind.Dynamic,
            "map-dynamic",
            "dynamic-map");

    private readonly WarApiMapSnapshotNormalizationCore _core;

    public WarApiDynamicMapNormalizationCoordinator(
        ICanonicalEvidenceReader evidenceReader,
        MapSnapshotKernel mapSnapshots,
        NormalizationKernel normalization,
        WarApiWorkerOptions options,
        TimeProvider timeProvider)
    {
        _core = new WarApiMapSnapshotNormalizationCore(
            evidenceReader,
            mapSnapshots,
            normalization,
            options,
            timeProvider);
    }

    public async Task<WarApiDynamicMapNormalizationResult> NormalizeAsync(
        SourceParseRunId sourceParseRunId,
        CancellationToken cancellationToken)
    {
        var result = await _core.NormalizeAsync(
            sourceParseRunId,
            Profile,
            cancellationToken);

        return new WarApiDynamicMapNormalizationResult(
            result.Status ==
                WarApiMapSnapshotNormalizationStatus.Normalized
                ? WarApiDynamicMapNormalizationStatus.Normalized
                : WarApiDynamicMapNormalizationStatus.Rejected,
            result.NormalizationRun,
            result.Snapshot);
    }
}
