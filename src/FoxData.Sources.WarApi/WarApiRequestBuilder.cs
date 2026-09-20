using System.Net.Http.Headers;
using FoxData.Sources.Abstractions;

namespace FoxData.Sources.WarApi;

public sealed class WarApiRequestBuilder
{
    public HttpRequestMessage Build(
        WarApiShard shard,
        SourceEndpoint endpoint,
        string? etag = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            WarApiCatalog.BuildUri(shard, endpoint));

        if (etag is not null)
        {
            if (!EntityTagHeaderValue.TryParse(etag, out var parsed))
            {
                request.Dispose();
                throw new ArgumentException(
                    "ETag must be a syntactically valid HTTP entity-tag.",
                    nameof(etag));
            }

            request.Headers.IfNoneMatch.Add(parsed);
        }

        return request;
    }
}

public static class WarApiHttpHandler
{
    public static SocketsHttpHandler Create(
        TimeSpan connectTimeout,
        int maxConnectionsPerServer = 8,
        int maxResponseHeadersLengthKiB = 32)
    {
        if (connectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connectTimeout),
                connectTimeout,
                "Connect timeout must be positive.");
        }

        if (maxConnectionsPerServer < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConnectionsPerServer),
                maxConnectionsPerServer,
                "Maximum connections must be positive.");
        }

        if (maxResponseHeadersLengthKiB < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResponseHeadersLengthKiB),
                maxResponseHeadersLengthKiB,
                "Maximum response header length must be positive.");
        }

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseCookies = false,
            Credentials = null,
            ConnectTimeout = connectTimeout,
            MaxConnectionsPerServer = maxConnectionsPerServer,
            MaxResponseHeadersLength = maxResponseHeadersLengthKiB,
        };
    }
}
