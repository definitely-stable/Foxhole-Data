using System.Net;
using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiResponsePolicyTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, false, WarApiResponseClass.SuccessfulRepresentation)]
    [InlineData(HttpStatusCode.NotModified, false, WarApiResponseClass.NotModified)]
    [InlineData(HttpStatusCode.Found, false, WarApiResponseClass.RedirectFailure)]
    [InlineData(HttpStatusCode.Unauthorized, false, WarApiResponseClass.AuthorizationFailure)]
    [InlineData(HttpStatusCode.Forbidden, false, WarApiResponseClass.AuthorizationFailure)]
    [InlineData(HttpStatusCode.NotFound, false, WarApiResponseClass.RootContractFailure)]
    [InlineData(HttpStatusCode.NotFound, true, WarApiResponseClass.MapUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, false, WarApiResponseClass.RetryableFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, WarApiResponseClass.ServerFailure)]
    [InlineData(HttpStatusCode.BadRequest, false, WarApiResponseClass.ClientFailure)]
    public void ClassifiesStatus(
        HttpStatusCode status,
        bool mapScoped,
        WarApiResponseClass expected)
    {
        Assert.Equal(expected, WarApiResponsePolicy.Classify(status, mapScoped));
    }

    [Fact]
    public void BackoffIsDeterministicBoundedAndVersioned()
    {
        var first = WarApiResponsePolicy.Backoff(3, "live-1/war");
        var repeated = WarApiResponsePolicy.Backoff(3, "live-1/war");
        var capped = WarApiResponsePolicy.Backoff(50, "live-1/war");

        Assert.Equal(first, repeated);
        Assert.InRange(first, TimeSpan.FromSeconds(48), TimeSpan.FromSeconds(72));
        Assert.InRange(capped, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
        Assert.Equal("warapi-backoff@1", WarApiResponsePolicy.BackoffPolicyVersion);
    }

    [Fact]
    public void SpreadIsStableAndInsideWindow()
    {
        var window = TimeSpan.FromMinutes(1);

        var first = WarApiResponsePolicy.Spread(
            "live-1",
            "map-dynamic/DeadLandsHex",
            window);
        var repeated = WarApiResponsePolicy.Spread(
            "live-1",
            "map-dynamic/DeadLandsHex",
            window);

        Assert.Equal(first, repeated);
        Assert.InRange(first, TimeSpan.Zero, window - TimeSpan.FromTicks(1));
    }

    [Theory]
    [InlineData("runtime-war-state", 60)]
    [InlineData("region-war-report", 60)]
    [InlineData("dynamic-map-state", 60)]
    [InlineData("active-map-list", 300)]
    [InlineData("static-map-state", 21600)]
    public void CadenceMatchesM3BootstrapPolicy(string capability, int expectedSeconds)
    {
        var value = capability switch
        {
            "runtime-war-state" => WarApiCapabilities.RuntimeWarState,
            "region-war-report" => WarApiCapabilities.RegionWarReport,
            "dynamic-map-state" => WarApiCapabilities.DynamicMapState,
            "active-map-list" => WarApiCapabilities.ActiveMapList,
            "static-map-state" => WarApiCapabilities.StaticMapState,
            _ => throw new InvalidOperationException(),
        };

        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            WarApiResponsePolicy.Cadence(value));
    }
}
