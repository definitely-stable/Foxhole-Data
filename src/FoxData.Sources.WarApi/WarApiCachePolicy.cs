using System.Net;
using System.Net.Http.Headers;

namespace FoxData.Sources.WarApi;

public sealed record WarApiCacheMetadata(
    HttpStatusCode StatusCode,
    string? CacheControl,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? SourceDate,
    long? AgeSeconds,
    string? RetryAfter);

public sealed record WarApiCacheDecision(
    bool ReusableRepresentation,
    DateTimeOffset SourceCacheEligibleAt,
    DateTimeOffset? RetryEligibleAt,
    DateTimeOffset NextEligibleAt);

public sealed class WarApiCachePolicy
{
    public WarApiCacheDecision Evaluate(
        HttpResponseMessage response,
        DateTimeOffset retrievedAt,
        TimeSpan localCadence)
    {
        ArgumentNullException.ThrowIfNull(response);

        return Evaluate(
            new WarApiCacheMetadata(
                response.StatusCode,
                response.Headers.CacheControl?.ToString(),
                response.Content.Headers.Expires,
                response.Headers.Date,
                response.Headers.Age is { } age
                    ? checked((long)age.TotalSeconds)
                    : null,
                response.Headers.RetryAfter?.ToString()),
            retrievedAt,
            localCadence);
    }

    public WarApiCacheDecision Evaluate(
        WarApiCacheMetadata metadata,
        DateTimeOffset retrievedAt,
        TimeSpan localCadence)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (localCadence < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localCadence),
                localCadence,
                "Local cadence must not be negative.");
        }

        CacheControlHeaderValue? cacheControl = null;
        if (metadata.CacheControl is not null)
        {
            _ = CacheControlHeaderValue.TryParse(
                metadata.CacheControl,
                out cacheControl);
        }

        RetryConditionHeaderValue? retryAfter = null;
        if (metadata.RetryAfter is not null)
        {
            _ = RetryConditionHeaderValue.TryParse(
                metadata.RetryAfter,
                out retryAfter);
        }

        var reusable =
            metadata.StatusCode == HttpStatusCode.OK &&
            cacheControl?.NoStore is not true;

        var sourceEligibleAt = CalculateSourceEligibility(
            cacheControl,
            metadata.ExpiresAt,
            metadata.SourceDate,
            metadata.AgeSeconds,
            retrievedAt);

        var retryEligibleAt = CalculateRetryEligibility(
            retryAfter,
            retrievedAt);

        var localTargetAt = retrievedAt + localCadence;
        var nextEligibleAt = Max(
            sourceEligibleAt,
            localTargetAt,
            retryEligibleAt ?? DateTimeOffset.MinValue);

        return new WarApiCacheDecision(
            reusable,
            sourceEligibleAt,
            retryEligibleAt,
            nextEligibleAt);
    }

    private static DateTimeOffset CalculateSourceEligibility(
        CacheControlHeaderValue? cacheControl,
        DateTimeOffset? expiresAt,
        DateTimeOffset? sourceDate,
        long? ageSeconds,
        DateTimeOffset retrievedAt)
    {
        if (cacheControl?.NoCache is true)
        {
            return retrievedAt;
        }

        var freshnessLifetime =
            cacheControl?.SharedMaxAge ??
            cacheControl?.MaxAge ??
            CalculateExpiresLifetime(
                expiresAt,
                sourceDate,
                retrievedAt);

        if (freshnessLifetime is null || freshnessLifetime <= TimeSpan.Zero)
        {
            return retrievedAt;
        }

        var currentAge = CalculateCurrentAge(
            sourceDate,
            ageSeconds,
            retrievedAt);
        var remaining = freshnessLifetime.Value - currentAge;

        return remaining > TimeSpan.Zero
            ? retrievedAt + remaining
            : retrievedAt;
    }

    private static TimeSpan? CalculateExpiresLifetime(
        DateTimeOffset? expiresAt,
        DateTimeOffset? sourceDate,
        DateTimeOffset retrievedAt)
    {
        if (expiresAt is null)
        {
            return null;
        }

        var basis = sourceDate ?? retrievedAt;
        var lifetime = expiresAt.Value - basis;

        return lifetime > TimeSpan.Zero ? lifetime : TimeSpan.Zero;
    }

    private static TimeSpan CalculateCurrentAge(
        DateTimeOffset? sourceDate,
        long? ageSeconds,
        DateTimeOffset retrievedAt)
    {
        var headerAge = ageSeconds is > 0
            ? TimeSpan.FromSeconds(ageSeconds.Value)
            : TimeSpan.Zero;
        var apparentAge = sourceDate is { } date && retrievedAt > date
            ? retrievedAt - date
            : TimeSpan.Zero;

        return headerAge > apparentAge ? headerAge : apparentAge;
    }

    private static DateTimeOffset? CalculateRetryEligibility(
        RetryConditionHeaderValue? retryAfter,
        DateTimeOffset retrievedAt)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return retrievedAt + delta;
        }

        if (retryAfter?.Date is { } date)
        {
            return date > retrievedAt ? date : retrievedAt;
        }

        return null;
    }

    private static DateTimeOffset Max(
        DateTimeOffset first,
        DateTimeOffset second,
        DateTimeOffset third)
    {
        var result = first > second ? first : second;
        return result > third ? result : third;
    }
}
