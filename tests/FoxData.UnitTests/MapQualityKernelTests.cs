using FoxData.Application.Canonical;
using FoxData.Application.Quality;
using FoxData.Core.Evidence;
using FoxData.Core.Quality;
using FoxData.Core.Runtime;

namespace FoxData.UnitTests;

public sealed class MapQualityKernelTests
{
    [Fact]
    public async Task ValidQualityWriteReachesStore()
    {
        var store = new RecordingStore();
        var kernel = new MapQualityKernel(store);
        var write = CreateWrite();

        _ = await kernel.RecordAsync(
            write,
            TestContext.Current.CancellationToken);

        Assert.Same(write, store.RecordedWrite);
    }

    [Fact]
    public async Task FindingRejectsUnknownEffectAndDualOccurrenceReference()
    {
        var kernel = new MapQualityKernel(new RecordingStore());

        var unknownEffect = CreateWrite() with
        {
            Findings =
            [
                CreateFinding() with
                {
                    Effect = "maybe",
                },
            ],
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => kernel.RecordAsync(
                unknownEffect,
                TestContext.Current.CancellationToken));

        var dualReference = CreateWrite() with
        {
            Findings =
            [
                CreateFinding() with
                {
                    MapItemOccurrenceId =
                        MapItemOccurrenceId.New(),
                    MapTextOccurrenceId =
                        MapTextOccurrenceId.New(),
                },
            ],
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => kernel.RecordAsync(
                dualReference,
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not-json")]
    [InlineData("{\"value\":1,\"value\":2}")]
    public async Task FindingRejectsNonObjectMalformedOrDuplicateMetrics(
        string metrics)
    {
        var kernel = new MapQualityKernel(new RecordingStore());
        var write = CreateWrite() with
        {
            Findings =
            [
                CreateFinding() with
                {
                    InputMetricsJson = metrics,
                },
            ],
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => kernel.RecordAsync(
                write,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QualityWriteRejectsInvalidTimingAndVersionIdentity()
    {
        var kernel = new MapQualityKernel(new RecordingStore());
        var write = CreateWrite();

        await Assert.ThrowsAsync<ArgumentException>(
            () => kernel.RecordAsync(
                write with
                {
                    CompletedAt = write.StartedAt.AddTicks(-1),
                },
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(
            () => kernel.RecordAsync(
                write with
                {
                    QualityPolicyVersion =
                        " warapi-map-quality@1 ",
                },
                TestContext.Current.CancellationToken));
    }

    private static MapQualityWrite CreateWrite()
    {
        var now = DateTimeOffset.UtcNow;
        return new MapQualityWrite(
            MapSnapshotId.New(),
            WarRegionId.New(),
            FetchId.New(),
            "warapi-map-taxonomy@1",
            "warapi-map-quality@1",
            BaselineMapObservationId: null,
            MapQualityDecision.Accepted,
            now,
            now.AddMilliseconds(1),
            MapSnapshotKind.Dynamic,
            now,
            SourceUpdatedAt: null,
            [CreateFinding()]);
    }

    private static MapQualityFindingCandidate CreateFinding() =>
        new(
            "taxonomy.unknown-icon",
            "taxonomy.unknown-icon@1",
            "taxonomy.unknown-icon-config@1",
            "informational",
            MapItemOccurrenceId: null,
            MapTextOccurrenceId: null,
            DetailCode: "unknown_icon",
            InputMetricsJson:
                "{\"rawIconType\":97,\"taxonomyVersion\":\"warapi-map-taxonomy@1\"}");

    private sealed class RecordingStore : IMapQualityStore
    {
        public MapQualityWrite? RecordedWrite { get; private set; }

        public Task<MapQualityResult?> GetAsync(
            MapSnapshotId mapSnapshotId,
            WarRegionId warRegionId,
            FetchId validationFetchId,
            string taxonomyVersion,
            string qualityPolicyVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<MapQualityResult?>(null);

        public Task<MapQualityResult> RecordAsync(
            MapQualityWrite write,
            CancellationToken cancellationToken)
        {
            RecordedWrite = write;

            var run = new MapQualityRunDescriptor(
                MapQualityRunId.New(),
                write.MapSnapshotId,
                write.WarRegionId,
                write.ValidationFetchId,
                write.TaxonomyVersion,
                write.QualityPolicyVersion,
                write.BaselineMapObservationId,
                write.Decision,
                write.StartedAt,
                write.CompletedAt,
                write.CompletedAt);

            return Task.FromResult(
                new MapQualityResult(
                    run,
                    [],
                    write.Decision ==
                        MapQualityDecision.Accepted
                        ? new MapObservationDescriptor(
                            MapObservationId.New(),
                            write.WarRegionId,
                            write.MapSnapshotId,
                            run.Id,
                            write.ValidationFetchId,
                            write.Kind,
                            write.ObservedAt,
                            write.SourceUpdatedAt,
                            write.CompletedAt)
                        : null));
        }
    }
}
