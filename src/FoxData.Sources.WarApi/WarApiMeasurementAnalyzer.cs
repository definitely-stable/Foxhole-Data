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
    long? SourceVersion = null,
    bool ValidationHit = false);

public sealed record WarApiEndpointMeasurementSummary(
    string EndpointKey,
    string CapabilityKey,
    int SampleCount,
    int OkCount,
    int NotModifiedCount,
    int ValidationHitCount,
    int OrphanNotModifiedCount,
    int OtherCount,
    int DuplicateOkCount,
    int RepresentationChangeCount,
    int SameEtagDifferentPayloadCount,
    int DifferentEtagSamePayloadCount,
    int VersionAdvanceCount,
    long VersionGapCount,
    int VersionRegressionCount,
    double? NotModifiedRatio,
    double? ValidationHitRatio,
    double? PayloadP50Bytes,
    double? PayloadP90Bytes,
    double? PayloadP95Bytes,
    double? PayloadP99Bytes,
    double? PayloadMaxBytes,
    double? DurationP50Ms,
    double? DurationP90Ms,
    double? DurationP95Ms,
    double? DurationP99Ms,
    double? DurationMaxMs,
    double? PollIntervalP50Seconds,
    double? PollIntervalP90Seconds,
    double? PollIntervalP95Seconds,
    double? PollIntervalP99Seconds,
    double? PollIntervalMaxSeconds,
    double? RepresentationChangeIntervalP50Seconds,
    double? RepresentationChangeIntervalP90Seconds,
    double? RepresentationChangeIntervalP95Seconds,
    double? RepresentationChangeIntervalP99Seconds,
    double? RepresentationChangeIntervalMaxSeconds);

public sealed record WarApiDownsampleSummary(
    string EndpointKey,
    string CapabilityKey,
    TimeSpan CandidateCadence,
    int BaselineEpisodeCount,
    int CapturedEpisodeCount,
    int MissedEpisodeCount,
    int SimulatedRequestCount,
    double CaptureRatio,
    double? ObservationDelayP50Seconds,
    double? ObservationDelayP95Seconds,
    double? ObservationDelayP99Seconds);

public static class WarApiMeasurementAnalyzer
{
    public static WarApiEndpointMeasurementSummary AnalyzeEndpoint(
        IEnumerable<WarApiMeasurementSample> samples)
    {
        var ordered = OrderAndValidate(samples);

        var endpointKey = ordered[0].EndpointKey;
        var capability = ordered[0].Capability;

        var okCount = 0;
        var notModifiedCount = 0;
        var validationHitCount = 0;
        var orphanNotModifiedCount = 0;
        var otherCount = 0;
        var duplicateOkCount = 0;
        var representationChangeCount = 0;
        var sameEtagDifferentPayloadCount = 0;
        var differentEtagSamePayloadCount = 0;
        var versionAdvanceCount = 0;
        long versionGapCount = 0;
        var versionRegressionCount = 0;

        string? previousPayloadHash = null;
        string? effectiveValidatorEtag = null;
        long? previousVersion = null;
        DateTimeOffset? previousRepresentationChangeAt = null;

        var payloadSizes = new List<long>();
        var durations = new List<long>(ordered.Length);
        var pollIntervals = new List<double>(Math.Max(0, ordered.Length - 1));
        var representationChangeIntervals = new List<double>();

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

                            if (previousRepresentationChangeAt is { } previousChangeAt)
                            {
                                representationChangeIntervals.Add(
                                    (sample.RequestStartedAt - previousChangeAt)
                                    .TotalSeconds);
                            }

                            previousRepresentationChangeAt =
                                sample.RequestStartedAt;

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
                            versionAdvanceCount++;
                            versionGapCount = SaturatingAdd(
                                versionGapCount,
                                MissingVersions(
                                    oldVersion,
                                    currentVersion));
                        }
                    }

                    if (previousPayloadHash is null)
                    {
                        previousRepresentationChangeAt =
                            sample.RequestStartedAt;
                    }

                    previousPayloadHash = sample.PayloadHash;
                    effectiveValidatorEtag = sample.SourceEtag;
                    previousVersion = sample.SourceVersion;
                }
            }
            else if (sample.StatusCode == 304)
            {
                notModifiedCount++;

                if (sample.ValidationHit)
                {
                    validationHitCount++;
                }
                else
                {
                    orphanNotModifiedCount++;
                }

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

        var notModifiedDenominator = okCount + notModifiedCount;

        return new WarApiEndpointMeasurementSummary(
            endpointKey,
            capability.Key,
            ordered.Length,
            okCount,
            notModifiedCount,
            validationHitCount,
            orphanNotModifiedCount,
            otherCount,
            duplicateOkCount,
            representationChangeCount,
            sameEtagDifferentPayloadCount,
            differentEtagSamePayloadCount,
            versionAdvanceCount,
            versionGapCount,
            versionRegressionCount,
            notModifiedDenominator == 0
                ? null
                : (double)notModifiedCount / notModifiedDenominator,
            notModifiedCount == 0
                ? null
                : (double)validationHitCount / notModifiedCount,
            PercentileCont(payloadSizes, 0.50),
            PercentileCont(payloadSizes, 0.90),
            PercentileCont(payloadSizes, 0.95),
            PercentileCont(payloadSizes, 0.99),
            MaxOrNull(payloadSizes),
            PercentileCont(durations, 0.50),
            PercentileCont(durations, 0.90),
            PercentileCont(durations, 0.95),
            PercentileCont(durations, 0.99),
            MaxOrNull(durations),
            PercentileCont(pollIntervals, 0.50),
            PercentileCont(pollIntervals, 0.90),
            PercentileCont(pollIntervals, 0.95),
            PercentileCont(pollIntervals, 0.99),
            MaxOrNull(pollIntervals),
            PercentileCont(representationChangeIntervals, 0.50),
            PercentileCont(representationChangeIntervals, 0.90),
            PercentileCont(representationChangeIntervals, 0.95),
            PercentileCont(representationChangeIntervals, 0.99),
            MaxOrNull(representationChangeIntervals));
    }

    public static WarApiDownsampleSummary SimulateCadence(
        IEnumerable<WarApiMeasurementSample> samples,
        TimeSpan candidateCadence)
    {
        if (candidateCadence <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidateCadence),
                candidateCadence,
                "Candidate cadence must be positive.");
        }

        var ordered = OrderAndValidate(samples);
        var episodes = new List<RepresentationEpisode>();
        string? effectivePayloadHash = null;
        var currentEpisodeIndex = -1;
        var sampleEpisodeIndexes = new int?[ordered.Length];

        for (var index = 0; index < ordered.Length; index++)
        {
            var sample = ordered[index];

            if (sample.StatusCode == 200 &&
                sample.PayloadHash is not null &&
                !string.Equals(
                    effectivePayloadHash,
                    sample.PayloadHash,
                    StringComparison.Ordinal))
            {
                effectivePayloadHash = sample.PayloadHash;
                currentEpisodeIndex = episodes.Count;
                episodes.Add(
                    new RepresentationEpisode(
                        sample.RequestStartedAt));
            }

            if (sample.StatusCode is 200 or 304 &&
                currentEpisodeIndex >= 0)
            {
                sampleEpisodeIndexes[index] = currentEpisodeIndex;
            }
        }

        if (episodes.Count == 0)
        {
            throw new ArgumentException(
                "Downsampling requires at least one body-bearing representation.",
                nameof(samples));
        }

        var capturedEpisodes = new HashSet<int>();
        var observationDelays = new List<double>();
        var simulatedRequestCount = 0;
        var nextTarget = ordered[0].RequestStartedAt;

        for (var index = 0; index < ordered.Length;)
        {
            while (index < ordered.Length &&
                   ordered[index].RequestStartedAt < nextTarget)
            {
                index++;
            }

            if (index >= ordered.Length)
            {
                break;
            }

            var selected = ordered[index];
            simulatedRequestCount++;

            if (sampleEpisodeIndexes[index] is { } episodeIndex &&
                capturedEpisodes.Add(episodeIndex))
            {
                var episode = episodes[episodeIndex];
                var observationDelay =
                    selected.RequestStartedAt - episode.StartedAt;
                observationDelays.Add(
                    observationDelay.TotalSeconds);
            }

            nextTarget = selected.RequestStartedAt + candidateCadence;
            index++;
        }

        var capturedCount = capturedEpisodes.Count;
        return new WarApiDownsampleSummary(
            ordered[0].EndpointKey,
            ordered[0].Capability.Key,
            candidateCadence,
            episodes.Count,
            capturedCount,
            episodes.Count - capturedCount,
            simulatedRequestCount,
            (double)capturedCount / episodes.Count,
            PercentileCont(observationDelays, 0.50),
            PercentileCont(observationDelays, 0.95),
            PercentileCont(observationDelays, 0.99));
    }

    private static WarApiMeasurementSample[] OrderAndValidate(
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

        }

        return ordered;
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

    private static long SaturatingAdd(
        long left,
        long right) =>
        left >= long.MaxValue - right
            ? long.MaxValue
            : left + right;

    private sealed record RepresentationEpisode(
        DateTimeOffset StartedAt);

    private static double? MaxOrNull(
        IEnumerable<long> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0
            ? null
            : materialized.Max();
    }

    private static double? MaxOrNull(
        IEnumerable<double> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0
            ? null
            : materialized.Max();
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
