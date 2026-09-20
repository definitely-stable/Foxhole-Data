using System.Net;
using System.Net.Http.Headers;
using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiCachePolicyTests
{
    private static readonly DateTimeOffset RetrievedAt =
        new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MaxAgeAndAgeDetermineSourceEligibility()
    {
        using var response = Response(HttpStatusCode.OK);
        response.Headers.Date = RetrievedAt.AddSeconds(-10);
        response.Headers.Age = TimeSpan.FromSeconds(20);
        response.Headers.CacheControl = new CacheControlHeaderValue
        {
            MaxAge = TimeSpan.FromSeconds(60),
        };

        var decision = new WarApiCachePolicy().Evaluate(
            response,
            RetrievedAt,
            localCadence: TimeSpan.FromSeconds(5));

        Assert.Equal(RetrievedAt.AddSeconds(40), decision.SourceCacheEligibleAt);
        Assert.Equal(RetrievedAt.AddSeconds(40), decision.NextEligibleAt);
        Assert.True(decision.ReusableRepresentation);
    }

    [Fact]
    public void SharedMaxAgeWinsOverMaxAge()
    {
        using var response = Response(HttpStatusCode.OK);
        response.Headers.Date = RetrievedAt;
        response.Headers.CacheControl = new CacheControlHeaderValue
        {
            SharedMaxAge = TimeSpan.FromSeconds(120),
            MaxAge = TimeSpan.FromSeconds(10),
        };

        var decision = new WarApiCachePolicy().Evaluate(
            response,
            RetrievedAt,
            localCadence: TimeSpan.FromSeconds(5));

        Assert.Equal(RetrievedAt.AddSeconds(120), decision.SourceCacheEligibleAt);
    }

    [Fact]
    public void NoCacheRequiresImmediateRevalidationButLocalCadenceStillApplies()
    {
        using var response = Response(HttpStatusCode.OK);
        response.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            MaxAge = TimeSpan.FromHours(1),
        };

        var decision = new WarApiCachePolicy().Evaluate(
            response,
            RetrievedAt,
            localCadence: TimeSpan.FromMinutes(1));

        Assert.Equal(RetrievedAt, decision.SourceCacheEligibleAt);
        Assert.Equal(RetrievedAt.AddMinutes(1), decision.NextEligibleAt);
    }

    [Fact]
    public void NoStorePreventsRepresentationReuse()
    {
        using var response = Response(HttpStatusCode.OK);
        response.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoStore = true,
            MaxAge = TimeSpan.FromMinutes(1),
        };

        var decision = new WarApiCachePolicy().Evaluate(
            response,
            RetrievedAt,
            localCadence: TimeSpan.Zero);

        Assert.False(decision.ReusableRepresentation);
    }

    [Fact]
    public void RetryAfterIsLowerBoundForNextEligibility()
    {
        using var response = Response(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(
            TimeSpan.FromMinutes(3));

        var decision = new WarApiCachePolicy().Evaluate(
            response,
            RetrievedAt,
            localCadence: TimeSpan.FromSeconds(30));

        Assert.Equal(RetrievedAt.AddMinutes(3), decision.RetryEligibleAt);
        Assert.Equal(RetrievedAt.AddMinutes(3), decision.NextEligibleAt);
        Assert.False(decision.ReusableRepresentation);
    }

    [Fact]
    public void ExpiresUsesResponseDateWhenPresent()
    {
        using var response = Response(HttpStatusCode.OK);
        response.Headers.Date = RetrievedAt.AddSeconds(-15);
        response.Content.Headers.Expires = RetrievedAt.AddSeconds(45);

        var decision = new WarApiCachePolicy().Evaluate(
            response,
            RetrievedAt,
            localCadence: TimeSpan.Zero);

        Assert.Equal(RetrievedAt.AddSeconds(45), decision.SourceCacheEligibleAt);
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode) =>
        new(statusCode)
        {
            Content = new ByteArrayContent([]),
        };
}
