using FoxData.Application.Canonical;
using FoxData.Core.Evidence;

namespace FoxData.UnitTests;

public sealed class MapSnapshotKernelTests
{
    [Fact]
    public async Task AcceptedSnapshotPreservesEqualOccurrencesAtDistinctOrdinals()
    {
        var store = new RecordingStore();
        var kernel = new MapSnapshotKernel(store);
        var now = DateTimeOffset.UtcNow;

        var duplicate = new MapItemOccurrenceCandidate(
            0,
            "WARDENS",
            56,
            0.5,
            0.25,
            0,
            0);

        var write = new MapSnapshotWrite(
            SourceParseRunId.New(),
            "map-normalizer@1",
            "dynamic-map-state",
            now,
            now.AddMilliseconds(1),
            FetchId.New(),
            MapSnapshotKind.Dynamic,
            "DeadLandsHex",
            -1,
            -1,
            -1,
            -1,
            null,
            [
                duplicate,
                duplicate with { SourceOrdinal = 1 },
            ],
            []);

        _ = await kernel.RecordAcceptedAsync(write);

        Assert.Same(write, store.RecordedWrite);
        Assert.Equal(2, store.RecordedWrite!.Items.Count);
        Assert.Equal(
            store.RecordedWrite.Items[0] with { SourceOrdinal = 1 },
            store.RecordedWrite.Items[1]);
    }

    [Fact]
    public async Task SnapshotRejectsOrdinalThatDoesNotMatchSourceArrayPosition()
    {
        var kernel = new MapSnapshotKernel(new RecordingStore());
        var now = DateTimeOffset.UtcNow;

        var write = new MapSnapshotWrite(
            SourceParseRunId.New(),
            "map-normalizer@1",
            "static-map-state",
            now,
            now,
            FetchId.New(),
            MapSnapshotKind.Static,
            "DeadLandsHex",
            null,
            null,
            null,
            null,
            null,
            [
                new MapItemOccurrenceCandidate(
                    1,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null),
            ],
            []);

        await Assert.ThrowsAsync<ArgumentException>(
            () => kernel.RecordAcceptedAsync(write));
    }

    [Fact]
    public async Task SnapshotRejectsInvalidStructuralIdentityButNotQualityValues()
    {
        var store = new RecordingStore();
        var kernel = new MapSnapshotKernel(store);
        var now = DateTimeOffset.UtcNow;

        var qualityDeferred = new MapSnapshotWrite(
            SourceParseRunId.New(),
            "map-normalizer@1",
            "dynamic-map-state",
            now,
            now,
            FetchId.New(),
            MapSnapshotKind.Dynamic,
            "DeadLandsHex",
            -99,
            -3,
            -7,
            long.MinValue,
            null,
            [
                new MapItemOccurrenceCandidate(
                    0,
                    "FUTURE_TEAM",
                    999,
                    double.NaN,
                    double.PositiveInfinity,
                    int.MinValue,
                    int.MaxValue),
            ],
            []);

        _ = await kernel.RecordAcceptedAsync(qualityDeferred);
        Assert.Same(
            qualityDeferred,
            store.RecordedWrite);

        var invalidIdentity = qualityDeferred with
        {
            SourceMapName = " DeadLandsHex ",
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => kernel.RecordAcceptedAsync(invalidIdentity));
    }

    private sealed class RecordingStore : IMapSnapshotStore
    {
        public MapSnapshotWrite? RecordedWrite { get; private set; }

        public Task<MapSnapshotResult?> GetByNormalizationRunAsync(
            NormalizationRunId normalizationRunId,
            CancellationToken cancellationToken) =>
            Task.FromResult<MapSnapshotResult?>(null);

        public Task<MapSnapshotResult> RecordAcceptedAsync(
            MapSnapshotWrite write,
            CancellationToken cancellationToken)
        {
            RecordedWrite = write;

            var run = new NormalizationRunDescriptor(
                NormalizationRunId.New(),
                write.SourceParseRunId,
                write.NormalizerVersion,
                NormalizationRunOutcome.Normalized,
                null,
                write.NormalizationStartedAt,
                write.NormalizationCompletedAt,
                write.NormalizationCompletedAt);
            var snapshot = new MapSnapshotDescriptor(
                MapSnapshotId.New(),
                run.Id,
                write.SourceParseRunId,
                write.RepresentationFetchId,
                write.Kind,
                write.SourceMapName,
                write.SourceRegionId,
                write.SourceScorchedVictoryTowns,
                write.SourceVersion,
                write.SourceLastUpdatedMs,
                write.SourceUpdatedAt,
                write.Items.Count,
                write.TextItems.Count,
                write.NormalizationCompletedAt);

            return Task.FromResult(
                new MapSnapshotResult(
                    run,
                    snapshot,
                    [],
                    []));
        }
    }
}
