using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Core.Runtime;

namespace FoxData.UnitTests;

public sealed class MapQualityKernelTests
{
    [Fact]
    public async Task ValidQualityWriteIsForwarded()
    {
        var store = new RecordingStore();
        var kernel = new MapQualityKernel(store);
        var now = DateTimeOffset.UtcNow;
        var write = CreateWrite(now);

        _ = await kernel.RecordAsync(
            write,
            TestContext.Current.CancellationToken);

        Assert.Same(write, store.RecordedWrite);
    }

    [Fact]
    public async Task FindingRejectsBothOccurrenceReferences()
    {
        var kernel = new MapQualityKernel(new RecordingStore());
        var now = DateTimeOffset.UtcNow;
        var finding = CreateFinding() with
        {
            MapItemOccurrenceId = MapItemOccurrenceId.New(),
            MapTextOccurrenceId = MapTextOccurrenceId.New(),
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => kernel.RecordAsync(
                CreateWrite(now) with
                {
                    Findings = [finding],
                },
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public async Task FindingRequiresJsonObjectMetrics(string metrics)
    {
        var kernel = new MapQualityKernel(new RecordingStore());
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<ArgumentException>(
            () => kernel.RecordAsync(
                CreateWrite(now) with
                {
                    Findings =
                    [
                        CreateFinding() with
                        {
                            InputMetricsJson = metrics,
                        },
                    ],
                },
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QualityRejectsCompletionBeforeStart()
    {
        var kernel = new MapQualityKernel(new RecordingStore());
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<ArgumentException>(
            () => kernel.RecordAsync(
                CreateWrite(now) with
                {
                    StartedAt = now,
                    CompletedAt = now.AddSeconds(-1),
                },
                TestContext.Current.CancellationToken));
    }

    private static MapQualityWrite CreateWrite(DateTimeOffset now) =>
        new(
            MapSnapshotId.New(),
            WarRegionId.New(),
            FetchId.New(),
            "warapi-map-taxonomy@1",
            "warapi-map-quality@1",
            null,
            MapQualityDecision.Accepted,
            now,
            now.AddMilliseconds(1),
            [CreateFinding()]);

    private static MapQualityFindingCandidate CreateFinding() =>
        new(
            "taxonomy.unknown-icon",
            "taxonomy.unknown-icon@1",
            "taxonomy.unknown-icon-config@1",
            MapQualityEffect.Informational,
            null,
            null,
            "unknown_icon",
            """{"count":1}""");

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
            var warRegion = new WarRegionDescriptor(
                write.WarRegionId,
                WarId.New(),
                RegionId.New(),
                "DeadLandsHex",
                null,
                write.StartedAt,
                write.StartedAt,
                write.StartedAt);
            MapObservationDescriptor? observation =
                write.Decision == MapQualityDecision.Accepted
                    ? new MapObservationDescriptor(
                        MapObservationId.New(),
                        write.WarRegionId,
                        write.MapSnapshotId,
                        run.Id,
                        write.ValidationFetchId,
                        MapSnapshotKind.Dynamic,
                        write.CompletedAt,
                        null,
                        write.CompletedAt)
                    : null;

            return Task.FromResult(
                new MapQualityResult(
                    run,
                    [],
                    observation,
                    warRegion));
        }
    }
}
