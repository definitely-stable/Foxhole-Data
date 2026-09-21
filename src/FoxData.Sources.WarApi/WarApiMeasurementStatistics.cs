using FoxData.Sources.Abstractions;

namespace FoxData.Sources.WarApi;

public sealed record WarApiParseMeasurementSample(
    string EndpointKey,
    SourceCapability Capability,
    DateTimeOffset RepresentationObservedAt,
    string Outcome,
    string? StructuralFingerprint,
    int UnknownPropertyCount,
    int UnknownCodeCount,
    long? SourceVersion,
    long? SourceLastUpdated,
    int? ContinuityGroup = null);

public sealed record WarApiParseMeasurementSummary(
    string EndpointKey,
    string CapabilityKey,
    int SampleCount,
    int ParsedCount,
    int ParsedWithUnknownsCount,
    int FailedCount,
    int StructuralFingerprintChangeCount,
    int SourceVersionAdvanceCount,
    long SourceVersionGapCount,
    int SourceVersionRegressionCount,
    int SourceLastUpdatedRegressionCount,
    long UnknownPropertyCount,
    long UnknownCodeCount);

public sealed record WarApiBurstMeasurementSummary(
    TimeSpan Window,
    int RequestCount,
    int BucketCount,
    double MeanRequestsPerBucket,
    double? P95RequestsPerBucket,
    double? P99RequestsPerBucket,
    int MaxRequestsPerBucket);

public static class WarApiMeasurementStatistics
{
    public static WarApiParseMeasurementSummary AnalyzeParseRuns(
        IEnumerable<WarApiParseMeasurementSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var ordered = samples
            .OrderBy(sample => sample.RepresentationObservedAt)
            .ToArray();

        if (ordered.Length == 0)
        {
            throw new ArgumentException(
                "At least one parse measurement sample is required.",
                nameof(samples));
        }

        var endpointKey = ordered[0].EndpointKey;
        var capability = ordered[0].Capability;
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointKey);

        var parsedCount = 0;
        var parsedWithUnknownsCount = 0;
        var failedCount = 0;
        var fingerprintChanges = 0;
        var sourceVersionAdvances = 0;
        long sourceVersionGaps = 0;
        var sourceVersionRegressions = 0;
        var sourceLastUpdatedRegressions = 0;
        long unknownProperties = 0;
        long unknownCodes = 0;

        string? previousFingerprint = null;
        long? previousSourceVersion = null;
        long? previousSourceLastUpdated = null;

        int? previousContinuityGroup = null;
        var hasPreviousSample = false;

        foreach (var sample in ordered)
        {
            if (hasPreviousSample &&
                sample.ContinuityGroup != previousContinuityGroup)
            {
                previousFingerprint = null;
                previousSourceVersion = null;
                previousSourceLastUpdated = null;
            }

            previousContinuityGroup = sample.ContinuityGroup;
            hasPreviousSample = true;

            if (!string.Equals(
                    endpointKey,
                    sample.EndpointKey,
                    StringComparison.Ordinal) ||
                sample.Capability != capability)
            {
                throw new ArgumentException(
                    "Parse measurement samples must belong to one endpoint and capability.",
                    nameof(samples));
            }

            if (sample.UnknownPropertyCount < 0 ||
                sample.UnknownCodeCount < 0)
            {
                throw new ArgumentException(
                    "Unknown counters must not be negative.",
                    nameof(samples));
            }

            switch (sample.Outcome)
            {
                case "parsed":
                    parsedCount++;
                    break;
                case "parsed_with_unknowns":
                    parsedWithUnknownsCount++;
                    break;
                default:
                    failedCount++;
                    break;
            }

            unknownProperties += sample.UnknownPropertyCount;
            unknownCodes += sample.UnknownCodeCount;

            if (sample.StructuralFingerprint is { } fingerprint)
            {
                if (previousFingerprint is not null &&
                    !string.Equals(
                        previousFingerprint,
                        fingerprint,
                        StringComparison.Ordinal))
                {
                    fingerprintChanges++;
                }

                previousFingerprint = fingerprint;
            }

            if (sample.SourceVersion is { } sourceVersion)
            {
                if (previousSourceVersion is { } previousVersion)
                {
                    if (sourceVersion < previousVersion)
                    {
                        sourceVersionRegressions++;
                    }
                    else if (sourceVersion > previousVersion)
                    {
                        sourceVersionAdvances++;
                        sourceVersionGaps = SaturatingAdd(
                            sourceVersionGaps,
                            MissingVersions(
                                previousVersion,
                                sourceVersion));
                    }
                }

                previousSourceVersion = sourceVersion;
            }

            if (sample.SourceLastUpdated is { } sourceLastUpdated)
            {
                if (previousSourceLastUpdated is { } previousLastUpdated &&
                    sourceLastUpdated < previousLastUpdated)
                {
                    sourceLastUpdatedRegressions++;
                }

                previousSourceLastUpdated = sourceLastUpdated;
            }
        }

        return new WarApiParseMeasurementSummary(
            endpointKey,
            capability.Key,
            ordered.Length,
            parsedCount,
            parsedWithUnknownsCount,
            failedCount,
            fingerprintChanges,
            sourceVersionAdvances,
            sourceVersionGaps,
            sourceVersionRegressions,
            sourceLastUpdatedRegressions,
            unknownProperties,
            unknownCodes);
    }

    public static WarApiBurstMeasurementSummary AnalyzeBurstShape(
        IEnumerable<DateTimeOffset> requestStarts,
        TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(requestStarts);

        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(window),
                window,
                "Burst window must be positive.");
        }

        var windowMilliseconds = window.TotalMilliseconds;
        if (windowMilliseconds < 1 ||
            windowMilliseconds != Math.Truncate(windowMilliseconds))
        {
            throw new ArgumentException(
                "Burst window must be a whole number of milliseconds.",
                nameof(window));
        }

        var width = checked((long)windowMilliseconds);
        var buckets = requestStarts
            .Select(timestamp =>
                FloorDiv(
                    timestamp.ToUnixTimeMilliseconds(),
                    width))
            .GroupBy(bucket => bucket)
            .Select(group => group.Count())
            .Order()
            .ToArray();

        if (buckets.Length == 0)
        {
            return new WarApiBurstMeasurementSummary(
                window,
                0,
                0,
                0,
                null,
                null,
                0);
        }

        var requestCount = buckets.Sum();
        return new WarApiBurstMeasurementSummary(
            window,
            requestCount,
            buckets.Length,
            (double)requestCount / buckets.Length,
            PercentileCont(buckets, 0.95),
            PercentileCont(buckets, 0.99),
            buckets[^1]);
    }

    private static long MissingVersions(
        long previousVersion,
        long currentVersion)
    {
        if (currentVersion <= previousVersion)
        {
            return 0;
        }

        var difference =
            (decimal)currentVersion -
            previousVersion -
            1;

        if (difference <= 0)
        {
            return 0;
        }

        return difference >= long.MaxValue
            ? long.MaxValue
            : (long)difference;
    }

    private static long SaturatingAdd(long left, long right) =>
        left >= long.MaxValue - right
            ? long.MaxValue
            : left + right;

    private static long FloorDiv(long value, long divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;

        return remainder < 0
            ? quotient - 1
            : quotient;
    }

    private static double? PercentileCont(
        IEnumerable<int> values,
        double percentile)
    {
        var ordered = values
            .Select(static value => (double)value)
            .Order()
            .ToArray();

        if (ordered.Length == 0)
        {
            return null;
        }

        var position = (ordered.Length - 1) * percentile;
        var lowerIndex = (int)Math.Floor(position);
        var upperIndex = (int)Math.Ceiling(position);

        if (lowerIndex == upperIndex)
        {
            return ordered[lowerIndex];
        }

        var fraction = position - lowerIndex;
        return ordered[lowerIndex] +
            ((ordered[upperIndex] - ordered[lowerIndex]) * fraction);
    }
}
