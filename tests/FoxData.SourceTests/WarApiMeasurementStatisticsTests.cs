using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiMeasurementStatisticsTests
{
    [Fact]
    public void ParseStatisticsTrackDriftUnknownsAndSourceRegressions()
    {
        var start = DateTimeOffset.Parse("2026-09-20T12:00:00+00:00");

        var samples = new[]
        {
            Sample(start, "parsed", "shape-a", 0, 0, 10, 1_000),
            Sample(start.AddMinutes(1), "parsed_with_unknowns", "shape-a", 2, 1, 13, 2_000),
            Sample(start.AddMinutes(2), "incompatible_shape", "shape-b", 0, 0, 12, 1_500),
            Sample(start.AddMinutes(3), "parsed", "shape-c", 1, 0, -1, 3_000),
        };

        var summary = WarApiMeasurementStatistics.AnalyzeParseRuns(samples);

        Assert.Equal("map-dynamic/DeadLandsHex", summary.EndpointKey);
        Assert.Equal("dynamic-map-state", summary.CapabilityKey);
        Assert.Equal(4, summary.SampleCount);
        Assert.Equal(2, summary.ParsedCount);
        Assert.Equal(1, summary.ParsedWithUnknownsCount);
        Assert.Equal(1, summary.FailedCount);
        Assert.Equal(2, summary.StructuralFingerprintChangeCount);
        Assert.Equal(2, summary.SourceVersionGapCount);
        Assert.Equal(2, summary.SourceVersionRegressionCount);
        Assert.Equal(1, summary.SourceLastUpdatedRegressionCount);
        Assert.Equal(3, summary.UnknownPropertyCount);
        Assert.Equal(1, summary.UnknownCodeCount);
    }

    [Fact]
    public void ParseStatisticsSaturateExtremeVersionGap()
    {
        var start = DateTimeOffset.Parse("2026-09-20T12:00:00+00:00");

        var summary = WarApiMeasurementStatistics.AnalyzeParseRuns(
        [
            Sample(start, "parsed", "shape-a", 0, 0, long.MinValue, 1),
            Sample(start.AddMinutes(1), "parsed", "shape-a", 0, 0, long.MaxValue, 2),
        ]);

        Assert.Equal(long.MaxValue, summary.SourceVersionGapCount);
        Assert.Equal(0, summary.SourceVersionRegressionCount);
    }

    [Fact]
    public void BurstStatisticsUseUtcAlignedWindows()
    {
        var epoch = DateTimeOffset.FromUnixTimeSeconds(0);

        var summary = WarApiMeasurementStatistics.AnalyzeBurstShape(
        [
            epoch.AddMilliseconds(100),
            epoch.AddMilliseconds(900),
            epoch.AddMilliseconds(1_100),
            epoch.AddMilliseconds(1_200),
            epoch.AddMilliseconds(1_900),
            epoch.AddMilliseconds(2_100),
        ],
            TimeSpan.FromSeconds(1));

        Assert.Equal(6, summary.RequestCount);
        Assert.Equal(3, summary.BucketCount);
        Assert.Equal(2d, summary.MeanRequestsPerBucket);
        Assert.Equal(3, summary.MaxRequestsPerBucket);
        Assert.InRange(summary.P95RequestsPerBucket!.Value, 2.89, 2.91);
        Assert.InRange(summary.P99RequestsPerBucket!.Value, 2.97, 2.99);
    }

    [Fact]
    public void EmptyBurstStatisticsAreZero()
    {
        var summary = WarApiMeasurementStatistics.AnalyzeBurstShape(
            Array.Empty<DateTimeOffset>(),
            TimeSpan.FromSeconds(5));

        Assert.Equal(0, summary.RequestCount);
        Assert.Equal(0, summary.BucketCount);
        Assert.Equal(0d, summary.MeanRequestsPerBucket);
        Assert.Null(summary.P95RequestsPerBucket);
        Assert.Null(summary.P99RequestsPerBucket);
        Assert.Equal(0, summary.MaxRequestsPerBucket);
    }

    [Fact]
    public void MixedParseEndpointsAreRejected()
    {
        var start = DateTimeOffset.Parse("2026-09-20T12:00:00+00:00");
        var first = Sample(start, "parsed", "shape-a", 0, 0, 1, 1);
        var second = first with
        {
            EndpointKey = "map-dynamic/MarbanHollow",
            RepresentationObservedAt = start.AddMinutes(1),
        };

        Assert.Throws<ArgumentException>(
            () => WarApiMeasurementStatistics.AnalyzeParseRuns([first, second]));
    }

    private static WarApiParseMeasurementSample Sample(
        DateTimeOffset observedAt,
        string outcome,
        string? fingerprint,
        int unknownProperties,
        int unknownCodes,
        long? sourceVersion,
        long? sourceLastUpdated) =>
        new(
            "map-dynamic/DeadLandsHex",
            WarApiCapabilities.DynamicMapState,
            observedAt,
            outcome,
            fingerprint,
            unknownProperties,
            unknownCodes,
            sourceVersion,
            sourceLastUpdated);
}
