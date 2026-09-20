using System.Net;
using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiRequestBuilderTests
{
    [Fact]
    public void BuildsConditionalGetWithWeakEtagUnchanged()
    {
        var builder = new WarApiRequestBuilder();

        using var request = builder.Build(
            WarApiShard.Live1,
            WarApiCatalog.War(),
            "W/\"source-tag\"");

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "https://war-service-live.foxholeservices.com/api/worldconquest/war",
            request.RequestUri!.AbsoluteUri);
        Assert.Equal("W/\"source-tag\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
    }

    [Fact]
    public void InvalidEtagIsRejectedBeforeExchange()
    {
        var builder = new WarApiRequestBuilder();

        Assert.Throws<ArgumentException>(
            () => builder.Build(
                WarApiShard.Live1,
                WarApiCatalog.War(),
                "not-an-entity-tag"));
    }

    [Fact]
    public void HttpHandlerDisablesRedirectsCookiesAndDecompression()
    {
        using var handler = WarApiHttpHandler.Create(
            TimeSpan.FromSeconds(5),
            maxConnectionsPerServer: 4,
            maxResponseHeadersLengthKiB: 16);

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(TimeSpan.FromSeconds(5), handler.ConnectTimeout);
        Assert.Equal(4, handler.MaxConnectionsPerServer);
        Assert.Equal(16, handler.MaxResponseHeadersLength);
    }
}
