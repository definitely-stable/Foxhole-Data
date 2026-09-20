using FoxData.Sources.Abstractions;

namespace FoxData.Sources.WarApi;

public sealed record WarApiMeasurementSample(
    string EndpointKey,
    SourceCapability Capability,
    DateTimeOffset RequestStartedAt,
    int? StatusCode,
    string? PayloadHash,
    long? PayloadBytes,
    string? SourceEtag,
    long DurationMs,
    long? SourceVersion = null);

public sealed record WarApiEndpointMeasurementSummary(
    string EndpointKey,
    string CapabilityKey,
    int SampleCount,
    int OkCount,
    int NotModifiedCount,
    int OtherCount,
    int DuplicateOkCount,
    int RepresentationChangeCount,
    int SameEtagDifferentPayloadCount,
    int DifferentEtagSamePayloadCount,
    long VersionGapCount,
    int VersionRegressionCount,
    double? ValidationRatio,
    double? PayloadP50Bytes,
    double? PayloadP95Bytes,
    double? PayloadP99Bytes,
    double? DurationP50Ms,
    double? DurationP95Ms,
    double? DurationP99Ms,
    double? PollIntervalP50Seconds,
    double? PollIntervalP95Seconds,
    double? PollIntervalP99Seconds);

public static class WarApiMeasurementAnalyzer
{
    public static WarApiEndpointMeasurementSummary AnalyzeEndpoint(
        IEnumerable<WarApiMeasurementSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var ordered = samples
            .OrderBy(sample => sample.RequestStartedAt)
            .ToArray();

        if (ordered.Length == 0)
        {
            throw new ArgumentException(
                "At least one measurement sample is required.",
                nameof(samples));
        }

        var endpointKey = ordered[0].EndpointKey;
        var capability = ordered[0].Capability;

        ArgumentException.ThrowIfNullOrWhiteSpace(endpointKey);

        foreach (var sample in ordered)
        {
            if (!string.Equals(
                    endpointKey,
                    sample.EndpointKey,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Measurement samples must belong to one endpoint.",
                    nameof(samples));
            }

            if (sample.Capability != capability)
            {
                throw new ArgumentException(
                    "Measurement samples must belong to one capability.",
                    nameof(samples));
            }

            if (sample.DurationMs < 0)
            {
                throw new ArgumentException(
                    "Measurement duration must not be negative.",
                    nameof(samples));
            }

            if (sample.PayloadBytes < 0)
            {
                throw new ArgumentException(
                    "Payload size must not be negative.",
                    nameof(samples));
            }

            if (sample.SourceVersion < 0)
            {
                throw new ArgumentException(
                    "Source version must not be negative.",
                    nameof(samples));
            }
        }

        var okCount = 0;
        var notModifiedCount = 0;
        var otherCount = 0;
        var duplicateOkCount = 0;
        var representationChangeCount = 0;
        var sameEtagDifferentPayloadCount = 0;
        var differentEtagSamePayloadCount = 0;
        long versionGapCount = 0;
        var versionRegressionCount = 0;

        string? previousPayloadHash = null;
        string? effectiveValidatorEtag = null;
        long? previousVersion = null;

        var payloadSizes = new List<long>();
        var durations = new List<long>(ordered.Length);
        var pollIntervals = new List<double>(Math.Max(0, ordered.Length - 1));

        for (var index = 0; index < ordered.Length; index++)
        {
            var sample = ordered[index];
            durations.Add(sample.DurationMs);

            if (index > 0)
            {
                var pollInterval =
                    sample.RequestStartedAt -
                    ordered[index - 1].RequestStartedAt;
                pollIntervals.Add(pollInterval.TotalSeconds);
            }

            if (sample.StatusCode == 200)
            {
                okCount++;

                if (sample.PayloadBytes is { } payloadBytes)
                {
                    payloadSizes.Add(payloadBytes);
                }

                if (sample.PayloadHash is not null)
                {
                    if (previousPayloadHash is not null)
                    {
                        if (string.Equals(
                                previousPayloadHash,
                                sample.PayloadHash,
                                StringComparison.Ordinal))
                        {
                            duplicateOkCount++;

                            if (effectiveValidatorEtag is not null &&
                                sample.SourceEtag is not null &&
                                !string.Equals(
                                    effectiveValidatorEtag,
                                    sample.SourceEtag,
                                    StringComparison.Ordinal))
                            {
                                differentEtagSamePayloadCount++;
                            }
                        }
                        else
                        {
                            representationChangeCount++;

                            if (effectiveValidatorEtag is not null &&
                                sample.SourceEtag is not null &&
                                string.Equals(
                                    effectiveValidatorEtag,
                                    sample.SourceEtag,
                                    StringComparison.Ordinal))
                            {
                                sameEtagDifferentPayloadCount++;
                            }
                        }
                    }

                    if (previousVersion is { } oldVersion &&
                        sample.SourceVersion is { } currentVersion)
                    {
                        if (currentVersion < oldVersion)
                        {
                            versionRegressionCount++;
                        }
                        else if (currentVersion > oldVersion)
                        {
                            versionGapCount += currentVersion - oldVersion - 1;
                        }
                    }

                    previousPayloadHash = sample.PayloadHash;
                    effectiveValidatorEtag = sample.SourceEtag;
                    previousVersion = sample.SourceVersion;
                }
            }
            else if (sample.StatusCode == 304)
            {
                notModifiedCount++;

                if (sample.SourceEtag is not null)
                {
                    effectiveValidatorEtag = sample.SourceEtag;
                }
            }
            else
            {
                otherCount++;
            }
        }

        var validationDenominator = okCount + notModifiedCount;

        return new WarApiEndpointMeasurementSummary(
            endpointKey,
            capability.Key,
            ordered.Length,
            okCount,
            notModifiedCount,
            otherCount,
            duplicateOkCount,
            representationChangeCount,
            sameEtagDifferentPayloadCount,
            differentEtagSamePayloadCount,
            versionGapCount,
            versionRegressionCount,
            validationDenominator == 0
                ? null
                : (double)notModifiedCount / validationDenominator,
            PercentileCont(payloadSizes, 0.50),
            PercentileCont(payloadSizes, 0.95),
            PercentileCont(payloadSizes, 0.99),
            PercentileCont(durations, 0.50),
            PercentileCont(durations, 0.95),
            PercentileCont(durations, 0.99),
            PercentileCont(pollIntervals, 0.50),
            PercentileCont(pollIntervals, 0.95),
            PercentileCont(pollIntervals, 0.99));
    }

    internal static double? PercentileCont(
        IEnumerable<long> values,
        double percentile) =>
        PercentileCont(
            values.Select(static value => (double)value),
            percentile);

    internal static double? PercentileCont(
        IEnumerable<double> values,
        double percentile)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (percentile is < 0 or > 1 ||
            double.IsNaN(percentile))
        {
            throw new ArgumentOutOfRangeException(
                nameof(percentile),
                percentile,
                "Percentile must be between 0 and 1.");
        }

        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        if (ordered.Any(static value => !double.IsFinite(value)))
        {
            throw new ArgumentException(
                "Percentile values must be finite.",
                nameof(values));
        }

        if (ordered.Length == 1)
        {
            return ordered[0];
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
