using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiMeasurementAnalyzerTests
{
    [Fact]
    public void AnalyzerClassifiesRepresentationAndValidatorBehaviour()
    {
        var start = new DateTimeOffset(
            2026,
            9,
            20,
            12,
            0,
            0,
            TimeSpan.Zero);

        var samples = new[]
        {
            Sample(start, 200, "A", 100, "etag-a", 10, version: 10),
            Sample(start.AddSeconds(60), 304, null, null, "etag-b", 5),
            Sample(start.AddSeconds(120), 200, "A", 100, "etag-c", 20, version: 10),
            Sample(start.AddSeconds(180), 200, "B", 200, "etag-c", 30, version: 13),
            Sample(start.AddSeconds(240), 200, "C", 300, "etag-d", 40, version: 12),
            Sample(start.AddSeconds(300), 500, null, null, null, 50),
        };

        var summary = WarApiMeasurementAnalyzer.AnalyzeEndpoint(samples);

        Assert.Equal("map-dynamic/DeadLandsHex", summary.EndpointKey);
        Assert.Equal("dynamic-map-state", summary.CapabilityKey);
        Assert.Equal(6, summary.SampleCount);
        Assert.Equal(4, summary.OkCount);
        Assert.Equal(1, summary.NotModifiedCount);
        Assert.Equal(1, summary.OtherCount);
        Assert.Equal(1, summary.DuplicateOkCount);
        Assert.Equal(2, summary.RepresentationChangeCount);
        Assert.Equal(1, summary.SameEtagDifferentPayloadCount);
        Assert.Equal(1, summary.DifferentEtagSamePayloadCount);
        Assert.Equal(2, summary.VersionGapCount);
        Assert.Equal(1, summary.VersionRegressionCount);

        Assert.NotNull(summary.ValidationRatio);
        Assert.InRange(summary.ValidationRatio.Value, 0.199999, 0.200001);

        Assert.Equal(150d, summary.PayloadP50Bytes!.Value);
        Assert.Equal(270d, summary.PayloadP90Bytes!.Value);
        Assert.Equal(285d, summary.PayloadP95Bytes!.Value);
        Assert.Equal(297d, summary.PayloadP99Bytes!.Value);
        Assert.Equal(300d, summary.PayloadMaxBytes!.Value);

        Assert.Equal(25d, summary.DurationP50Ms!.Value);
        Assert.Equal(45d, summary.DurationP90Ms!.Value);
        Assert.Equal(47.5d, summary.DurationP95Ms!.Value);
        Assert.Equal(49.5d, summary.DurationP99Ms!.Value);
        Assert.Equal(50d, summary.DurationMaxMs!.Value);

        Assert.Equal(60d, summary.PollIntervalP50Seconds!.Value);
        Assert.Equal(60d, summary.PollIntervalP90Seconds!.Value);
        Assert.Equal(60d, summary.PollIntervalP95Seconds!.Value);
        Assert.Equal(60d, summary.PollIntervalP99Seconds!.Value);
        Assert.Equal(60d, summary.PollIntervalMaxSeconds!.Value);
    }

    [Fact]
    public void AnalyzerOrdersSamplesBeforeCalculatingPollIntervals()
    {
        var start = DateTimeOffset.Parse("2026-09-20T12:00:00+00:00");

        var samples = new[]
        {
            Sample(start.AddSeconds(120), 304, null, null, "etag-a", 10),
            Sample(start, 200, "A", 100, "etag-a", 10, version: 1),
            Sample(start.AddSeconds(60), 304, null, null, "etag-a", 10),
        };

        var summary = WarApiMeasurementAnalyzer.AnalyzeEndpoint(samples);

        Assert.Equal(60d, summary.PollIntervalP50Seconds!.Value);
        Assert.Equal(60d, summary.PollIntervalP95Seconds!.Value);
        Assert.Equal(60d, summary.PollIntervalP99Seconds!.Value);
    }

    [Fact]
    public void DownsamplingCountsRepresentationEpisodesNotUniqueHashes()
    {
        var start = DateTimeOffset.Parse("2026-09-20T12:00:00+00:00");

        var samples = new[]
        {
            Sample(start, 200, "A", 100, "etag-a", 10),
            Sample(start.AddSeconds(15), 200, "B", 100, "etag-b", 10),
            Sample(start.AddSeconds(30), 200, "A", 100, "etag-c", 10),
        };

        var summary = WarApiMeasurementAnalyzer.SimulateCadence(
            samples,
            TimeSpan.FromSeconds(30));

        Assert.Equal(3, summary.BaselineEpisodeCount);
        Assert.Equal(2, summary.CapturedEpisodeCount);
        Assert.Equal(1, summary.MissedEpisodeCount);
        Assert.Equal(2, summary.SimulatedRequestCount);
        Assert.InRange(summary.CaptureRatio, 0.666666, 0.666667);
        Assert.Equal(0d, summary.ObservationDelayP50Seconds!.Value);
        Assert.Equal(0d, summary.ObservationDelayP95Seconds!.Value);
        Assert.Equal(0d, summary.ObservationDelayP99Seconds!.Value);
    }

    [Fact]
    public void DownsamplingUsesFirstProbeAtOrAfterCandidateTarget()
    {
        var start = DateTimeOffset.Parse("2026-09-20T12:00:00+00:00");

        var samples = new[]
        {
            Sample(start, 200, "A", 100, "etag-a", 10),
            Sample(start.AddSeconds(15), 304, null, null, "etag-a", 10),
            Sample(start.AddSeconds(30), 200, "B", 100, "etag-b", 10),
            Sample(start.AddSeconds(45), 200, "C", 100, "etag-c", 10),
            Sample(start.AddSeconds(60), 304, null, null, "etag-c", 10),
            Sample(start.AddSeconds(75), 200, "D", 100, "etag-d", 10),
        };

        var summary = WarApiMeasurementAnalyzer.SimulateCadence(
            samples,
            TimeSpan.FromSeconds(30));

        Assert.Equal(4, summary.BaselineEpisodeCount);
        Assert.Equal(3, summary.CapturedEpisodeCount);
        Assert.Equal(1, summary.MissedEpisodeCount);
        Assert.Equal(3, summary.SimulatedRequestCount);
        Assert.Equal(0.75d, summary.CaptureRatio);
        Assert.Equal(0d, summary.ObservationDelayP50Seconds!.Value);
        Assert.InRange(summary.ObservationDelayP95Seconds!.Value, 13.499999, 13.500001);
        Assert.InRange(summary.ObservationDelayP99Seconds!.Value, 14.699999, 14.700001);
    }

    [Fact]
    public void DownsamplingRejectsNonPositiveCadenceAndMissingRepresentations()
    {
        var start = DateTimeOffset.Parse("2026-09-20T12:00:00+00:00");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => WarApiMeasurementAnalyzer.SimulateCadence(
                [Sample(start, 200, "A", 100, "etag-a", 10)],
                TimeSpan.Zero));

        Assert.Throws<ArgumentException>(
            () => WarApiMeasurementAnalyzer.SimulateCadence(
                [Sample(start, 500, null, null, null, 10)],
                TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void AnalyzerRejectsMixedEndpoints()
    {
        var start = DateTimeOffset.Parse("2026-09-20T12:00:00+00:00");
        var first = Sample(start, 200, "A", 100, "etag-a", 10);
        var second = first with
        {
            EndpointKey = "map-dynamic/MarbanHollow",
            RequestStartedAt = start.AddSeconds(60),
        };

        Assert.Throws<ArgumentException>(
            () => WarApiMeasurementAnalyzer.AnalyzeEndpoint([first, second]));
    }

    [Fact]
    public void AnalyzerRejectsNegativeDurationOrPayloadSize()
    {
        var start = DateTimeOffset.Parse("2026-09-20T12:00:00+00:00");

        Assert.Throws<ArgumentException>(
            () => WarApiMeasurementAnalyzer.AnalyzeEndpoint(
            [
                Sample(start, 200, "A", 100, "etag-a", -1),
            ]));

        Assert.Throws<ArgumentException>(
            () => WarApiMeasurementAnalyzer.AnalyzeEndpoint(
            [
                Sample(start, 200, "A", -1, "etag-a", 10),
            ]));
    }

    [Fact]
    public void EmptyAnalyzerInputIsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => WarApiMeasurementAnalyzer.AnalyzeEndpoint(
                Array.Empty<WarApiMeasurementSample>()));
    }

    private static WarApiMeasurementSample Sample(
        DateTimeOffset requestStartedAt,
        int? statusCode,
        string? payloadHash,
        long? payloadBytes,
        string? etag,
        long durationMs,
        long? version = null) =>
        new(
            "map-dynamic/DeadLandsHex",
            WarApiCapabilities.DynamicMapState,
            requestStartedAt,
            statusCode,
            payloadHash,
            payloadBytes,
            etag,
            durationMs,
            version);
}
