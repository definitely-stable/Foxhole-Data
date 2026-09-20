using FoxData.Sources.Abstractions;

namespace FoxData.Sources.WarApi;

public sealed record WarApiMeasurementSeries(
    string ShardKey,
    string EndpointKey,
    SourceCapability Capability,
    IReadOnlyList<WarApiMeasurementSample> Fetches,
    IReadOnlyList<WarApiParseMeasurementSample> ParseRuns);

public sealed record WarApiMeasuredEndpoint(
    string ShardKey,
    string EndpointKey,
    string CapabilityKey,
    WarApiEndpointMeasurementSummary Fetches,
    WarApiParseMeasurementSummary? Parsing);

public sealed record WarApiShardCapabilityMeasurementSummary(
    string ShardKey,
    string CapabilityKey,
    int EndpointCount,
    int FetchCount,
    int OkCount,
    int NotModifiedCount,
    int ValidationHitCount,
    int OrphanNotModifiedCount,
    int OtherCount,
    int DuplicateOkCount,
    int RepresentationChangeCount,
    int ParseRunCount,
    int ParseFailureCount,
    int StructuralFingerprintChangeCount,
    int SourceVersionAdvanceCount,
    long SourceVersionGapCount,
    int SourceVersionRegressionCount,
    int SourceLastUpdatedRegressionCount,
    double? NotModifiedRatio,
    double? ValidationHitRatio);

public sealed record WarApiMeasurementReport(
    IReadOnlyList<WarApiMeasuredEndpoint> Endpoints,
    IReadOnlyList<WarApiShardCapabilityMeasurementSummary> Groups,
    IReadOnlyList<WarApiBurstMeasurementSummary> BurstShape);

public static class WarApiMeasurementReportBuilder
{
    private static readonly TimeSpan[] BurstWindows =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(60),
    ];

    public static WarApiMeasurementReport Build(
        IEnumerable<WarApiMeasurementSeries> series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var materialized = series.ToArray();
        var endpoints = new List<WarApiMeasuredEndpoint>(materialized.Length);
        var allRequestStarts = new List<DateTimeOffset>();

        foreach (var item in materialized)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.ShardKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.EndpointKey);
            ArgumentNullException.ThrowIfNull(item.Fetches);
            ArgumentNullException.ThrowIfNull(item.ParseRuns);

            if (item.Fetches.Count == 0)
            {
                throw new ArgumentException(
                    $"Measurement series '{item.EndpointKey}' contains no fetch samples.",
                    nameof(series));
            }

            var fetchSummary =
                WarApiMeasurementAnalyzer.AnalyzeEndpoint(item.Fetches);

            if (!string.Equals(
                    fetchSummary.EndpointKey,
                    item.EndpointKey,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    fetchSummary.CapabilityKey,
                    item.Capability.Key,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Measurement series '{item.EndpointKey}' metadata does not match its fetch samples.",
                    nameof(series));
            }

            WarApiParseMeasurementSummary? parseSummary = null;
            if (item.ParseRuns.Count > 0)
            {
                parseSummary =
                    WarApiMeasurementStatistics.AnalyzeParseRuns(
                        item.ParseRuns);

                if (!string.Equals(
                        parseSummary.EndpointKey,
                        item.EndpointKey,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        parseSummary.CapabilityKey,
                        item.Capability.Key,
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Measurement series '{item.EndpointKey}' metadata does not match its parse samples.",
                        nameof(series));
                }
            }

            allRequestStarts.AddRange(
                item.Fetches.Select(fetch => fetch.RequestStartedAt));

            endpoints.Add(
                new WarApiMeasuredEndpoint(
                    item.ShardKey,
                    item.EndpointKey,
                    item.Capability.Key,
                    fetchSummary,
                    parseSummary));
        }

        var orderedEndpoints = endpoints
            .OrderBy(endpoint => endpoint.ShardKey, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.CapabilityKey, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.EndpointKey, StringComparer.Ordinal)
            .ToArray();

        var groups = orderedEndpoints
            .GroupBy(
                endpoint => (endpoint.ShardKey, endpoint.CapabilityKey))
            .Select(group =>
            {
                var ok = group.Sum(endpoint => endpoint.Fetches.OkCount);
                var notModified =
                    group.Sum(endpoint => endpoint.Fetches.NotModifiedCount);
                var responseDenominator = ok + notModified;
                var validationHits = group.Sum(
                    endpoint => endpoint.Fetches.ValidationHitCount);

                return new WarApiShardCapabilityMeasurementSummary(
                    group.Key.ShardKey,
                    group.Key.CapabilityKey,
                    group.Count(),
                    group.Sum(endpoint => endpoint.Fetches.SampleCount),
                    ok,
                    notModified,
                    validationHits,
                    group.Sum(
                        endpoint =>
                            endpoint.Fetches.OrphanNotModifiedCount),
                    group.Sum(endpoint => endpoint.Fetches.OtherCount),
                    group.Sum(endpoint => endpoint.Fetches.DuplicateOkCount),
                    group.Sum(
                        endpoint =>
                            endpoint.Fetches.RepresentationChangeCount),
                    group.Sum(
                        endpoint =>
                            endpoint.Parsing?.SampleCount ?? 0),
                    group.Sum(
                        endpoint =>
                            endpoint.Parsing?.FailedCount ?? 0),
                    group.Sum(
                        endpoint =>
                            endpoint.Parsing?
                                .StructuralFingerprintChangeCount ?? 0),
                    group.Sum(
                        endpoint =>
                            endpoint.Parsing?
                                .SourceVersionAdvanceCount ?? 0),
                    SaturatingSum(
                        group.Select(
                            endpoint =>
                                endpoint.Parsing?
                                    .SourceVersionGapCount ?? 0)),
                    group.Sum(
                        endpoint =>
                            endpoint.Parsing?
                                .SourceVersionRegressionCount ?? 0),
                    group.Sum(
                        endpoint =>
                            endpoint.Parsing?
                                .SourceLastUpdatedRegressionCount ?? 0),
                    responseDenominator == 0
                        ? null
                        : (double)notModified / responseDenominator,
                    notModified == 0
                        ? null
                        : (double)validationHits / notModified);
            })
            .OrderBy(group => group.ShardKey, StringComparer.Ordinal)
            .ThenBy(group => group.CapabilityKey, StringComparer.Ordinal)
            .ToArray();

        var burstShape = BurstWindows
            .Select(
                window =>
                    WarApiMeasurementStatistics.AnalyzeBurstShape(
                        allRequestStarts,
                        window))
            .ToArray();

        return new WarApiMeasurementReport(
            orderedEndpoints,
            groups,
            burstShape);
    }

    private static long SaturatingSum(IEnumerable<long> values)
    {
        var result = 0L;

        foreach (var value in values)
        {
            if (value < 0)
            {
                throw new ArgumentException(
                    "Saturating sum only accepts non-negative values.",
                    nameof(values));
            }

            if (result >= long.MaxValue - value)
            {
                return long.MaxValue;
            }

            result += value;
        }

        return result;
    }
}
