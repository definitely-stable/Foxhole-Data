using System.Net;
using System.Net.Http.Headers;

namespace FoxData.Sources.WarApi;

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

        if (localCadence < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localCadence),
                localCadence,
                "Local cadence must not be negative.");
        }

        var cacheControl = response.Headers.CacheControl;
        var reusable = IsSuccessfulRepresentation(response.StatusCode) &&
            cacheControl?.NoStore is not true;

        var sourceEligibleAt = CalculateSourceEligibility(
            response,
            retrievedAt);

        var retryEligibleAt = CalculateRetryEligibility(
            response.Headers.RetryAfter,
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
        HttpResponseMessage response,
        DateTimeOffset retrievedAt)
    {
        var cacheControl = response.Headers.CacheControl;
        if (cacheControl?.NoCache is true)
        {
            return retrievedAt;
        }

        var freshnessLifetime =
            cacheControl?.SharedMaxAge ??
            cacheControl?.MaxAge ??
            CalculateExpiresLifetime(response, retrievedAt);

        if (freshnessLifetime is null || freshnessLifetime <= TimeSpan.Zero)
        {
            return retrievedAt;
        }

        var currentAge = CalculateCurrentAge(response, retrievedAt);
        var remaining = freshnessLifetime.Value - currentAge;

        return remaining > TimeSpan.Zero
            ? retrievedAt + remaining
            : retrievedAt;
    }

    private static TimeSpan? CalculateExpiresLifetime(
        HttpResponseMessage response,
        DateTimeOffset retrievedAt)
    {
        var expires = response.Content.Headers.Expires;
        if (expires is null)
        {
            return null;
        }

        var basis = response.Headers.Date ?? retrievedAt;
        var lifetime = expires.Value - basis;

        return lifetime > TimeSpan.Zero ? lifetime : TimeSpan.Zero;
    }

    private static TimeSpan CalculateCurrentAge(
        HttpResponseMessage response,
        DateTimeOffset retrievedAt)
    {
        var headerAge = response.Headers.Age ?? TimeSpan.Zero;
        var apparentAge = response.Headers.Date is { } date && retrievedAt > date
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

    private static bool IsSuccessfulRepresentation(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.OK;

    private static DateTimeOffset Max(
        DateTimeOffset first,
        DateTimeOffset second,
        DateTimeOffset third)
    {
        var result = first > second ? first : second;
        return result > third ? result : third;
    }
}
