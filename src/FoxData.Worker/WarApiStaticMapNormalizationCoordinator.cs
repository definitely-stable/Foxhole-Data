using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Sources.WarApi;

namespace FoxData.Worker;

public enum WarApiStaticMapNormalizationStatus
{
    Normalized,
    Rejected,
}

public sealed record WarApiStaticMapNormalizationResult(
    WarApiStaticMapNormalizationStatus Status,
    NormalizationRunDescriptor NormalizationRun,
    MapSnapshotResult? Snapshot);

public sealed class WarApiStaticMapNormalizationCoordinator
{
    private static readonly WarApiMapSnapshotNormalizationProfile Profile =
        new(
            WarApiCapabilities.StaticMapState,
            "map-static/",
            WarApiVersions.StaticMapNormalizer,
            MapSnapshotKind.Static,
            "map-static",
            "static-map");

    private readonly WarApiMapSnapshotNormalizationCore _core;

    public WarApiStaticMapNormalizationCoordinator(
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

    public async Task<WarApiStaticMapNormalizationResult> NormalizeAsync(
        SourceParseRunId sourceParseRunId,
        CancellationToken cancellationToken)
    {
        var result = await _core.NormalizeAsync(
            sourceParseRunId,
            Profile,
            cancellationToken);

        return new WarApiStaticMapNormalizationResult(
            result.Status ==
                WarApiMapSnapshotNormalizationStatus.Normalized
                ? WarApiStaticMapNormalizationStatus.Normalized
                : WarApiStaticMapNormalizationStatus.Rejected,
            result.NormalizationRun,
            result.Snapshot);
    }
}
