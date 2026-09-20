using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiMeasurementReportTests
{
    [Fact]
    public void ReportAggregatesEndpointsByShardAndCapability()
    {
        var start = DateTimeOffset.Parse("2026-09-20T12:00:00+00:00");

        var first = new WarApiMeasurementSeries(
            "live-1",
            "map-dynamic/DeadLandsHex",
            WarApiCapabilities.DynamicMapState,
            [
                Fetch(
                    "map-dynamic/DeadLandsHex",
                    start,
                    200,
                    "A"),
                Fetch(
                    "map-dynamic/DeadLandsHex",
                    start.AddSeconds(60),
                    304,
                    null),
                Fetch(
                    "map-dynamic/DeadLandsHex",
                    start.AddSeconds(120),
                    200,
                    "B"),
            ],
            [
                Parse(
                    "map-dynamic/DeadLandsHex",
                    start,
                    "parsed",
                    "shape-a",
                    10,
                    1_000),
                Parse(
                    "map-dynamic/DeadLandsHex",
                    start.AddSeconds(120),
                    "parsed",
                    "shape-b",
                    12,
                    2_000),
            ]);

        var second = new WarApiMeasurementSeries(
            "live-1",
            "map-dynamic/MarbanHollow",
            WarApiCapabilities.DynamicMapState,
            [
                Fetch(
                    "map-dynamic/MarbanHollow",
                    start.AddSeconds(15),
                    200,
                    "X"),
                Fetch(
                    "map-dynamic/MarbanHollow",
                    start.AddSeconds(75),
                    200,
                    "X"),
            ],
            [
                Parse(
                    "map-dynamic/MarbanHollow",
                    start.AddSeconds(15),
                    "parsed_with_unknowns",
                    "shape-x",
                    4,
                    1_500),
            ]);

        var report = WarApiMeasurementReportBuilder.Build([second, first]);

        Assert.Equal(2, report.Endpoints.Count);
        Assert.Equal(
            "map-dynamic/DeadLandsHex",
            report.Endpoints[0].EndpointKey);
        Assert.Equal(
            "map-dynamic/MarbanHollow",
            report.Endpoints[1].EndpointKey);

        var group = Assert.Single(report.Groups);
        Assert.Equal("live-1", group.ShardKey);
        Assert.Equal("dynamic-map-state", group.CapabilityKey);
        Assert.Equal(2, group.EndpointCount);
        Assert.Equal(5, group.FetchCount);
        Assert.Equal(4, group.OkCount);
        Assert.Equal(1, group.NotModifiedCount);
        Assert.Equal(1, group.DuplicateOkCount);
        Assert.Equal(1, group.RepresentationChangeCount);
        Assert.Equal(3, group.ParseRunCount);
        Assert.Equal(0, group.ParseFailureCount);
        Assert.Equal(1, group.StructuralFingerprintChangeCount);
        Assert.Equal(1, group.SourceVersionGapCount);
        Assert.Equal(0, group.SourceVersionRegressionCount);
        Assert.Equal(0, group.SourceLastUpdatedRegressionCount);
        Assert.NotNull(group.ValidationRatio);
        Assert.InRange(group.ValidationRatio.Value, 0.199999, 0.200001);

        Assert.Equal(
            new[] { 1d, 5d, 10d, 60d },
            report.BurstShape
                .Select(summary => summary.Window.TotalSeconds)
                .ToArray());
        Assert.All(
            report.BurstShape,
            summary => Assert.Equal(5, summary.RequestCount));
    }

    [Fact]
    public void ReportRejectsSeriesMetadataThatDoesNotMatchSamples()
    {
        var start = DateTimeOffset.Parse("2026-09-20T12:00:00+00:00");
        var series = new WarApiMeasurementSeries(
            "live-1",
            "map-dynamic/DeadLandsHex",
            WarApiCapabilities.DynamicMapState,
            [
                Fetch(
                    "map-dynamic/MarbanHollow",
                    start,
                    200,
                    "A"),
            ],
            []);

        Assert.Throws<ArgumentException>(
            () => WarApiMeasurementReportBuilder.Build([series]));
    }

    private static WarApiMeasurementSample Fetch(
        string endpointKey,
        DateTimeOffset observedAt,
        int statusCode,
        string? payloadHash) =>
        new(
            endpointKey,
            WarApiCapabilities.DynamicMapState,
            observedAt,
            statusCode,
            payloadHash,
            payloadHash is null ? null : 100,
            "etag",
            10);

    private static WarApiParseMeasurementSample Parse(
        string endpointKey,
        DateTimeOffset observedAt,
        string outcome,
        string fingerprint,
        long version,
        long lastUpdated) =>
        new(
            endpointKey,
            WarApiCapabilities.DynamicMapState,
            observedAt,
            outcome,
            fingerprint,
            0,
            0,
            version,
            lastUpdated);
}
