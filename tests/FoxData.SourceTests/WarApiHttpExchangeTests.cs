using System.Net;
using System.Net.Http.Headers;
using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiHttpExchangeTests
{
    [Fact]
    public async Task ExchangeIssuesExactlyOneApplicationSend()
    {
        var handler = new CountingHandler(
            request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    RequestMessage = request,
                    Content = new StringContent("temporary"),
                };
                return response;
            });

        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://war-service-live.foxholeservices.com/api/worldconquest/war");

        var exchange = new WarApiHttpExchange(
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            maximumBodyBytes: 1024);

        var result = await exchange.SendAsync(
            client,
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.SendCount);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.Equal("temporary", System.Text.Encoding.UTF8.GetString(result.Body!));
    }

    [Fact]
    public async Task NotModifiedDoesNotCreateBody()
    {
        var handler = new CountingHandler(
            request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.NotModified)
                {
                    RequestMessage = request,
                    Content = new StringContent("must-not-be-used"),
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
                return response;
            });

        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://war-service-live.foxholeservices.com/api/worldconquest/war");

        var exchange = new WarApiHttpExchange(
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            maximumBodyBytes: 1024);

        var result = await exchange.SendAsync(
            client,
            request,
            TestContext.Current.CancellationToken);

        Assert.Null(result.Body);
        Assert.Equal("\"abc\"", result.Etag);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task DeclaredOversizedBodyIsRejected()
    {
        var handler = new CountingHandler(
            request =>
            {
                var content = new ByteArrayContent(new byte[8]);
                content.Headers.ContentLength = 8;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = content,
                };
            });

        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://war-service-live.foxholeservices.com/api/worldconquest/war");

        var exchange = new WarApiHttpExchange(
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            maximumBodyBytes: 4);

        var result = await exchange.SendAsync(
            client,
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Null(result.Body);
        Assert.Equal("body_limit_exceeded", result.BodyErrorCode);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task MidBodyTransportFailureRetainsResponseMetadata()
    {
        var handler = new CountingHandler(
            request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StreamContent(new FailingReadStream()),
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"partial-v1\"");
                response.Headers.CacheControl = CacheControlHeaderValue.Parse("max-age=60");
                return response;
            });

        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://war-service-live.foxholeservices.com/api/worldconquest/war");

        var exchange = new WarApiHttpExchange(
            TimeProvider.System,
            TimeSpan.FromSeconds(5),
            maximumBodyBytes: 1024);

        var result = await exchange.SendAsync(
            client,
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("\"partial-v1\"", result.Etag);
        Assert.Equal("max-age=60", result.CacheControl);
        Assert.Null(result.Body);
        Assert.Equal("body_read_failed", result.BodyErrorCode);
        Assert.Equal(1, handler.SendCount);
    }

    private sealed class FailingReadStream : Stream
    {
        private bool _returnedPartial;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_returnedPartial)
            {
                throw new HttpRequestException("Synthetic mid-body failure.");
            }

            _returnedPartial = true;
            var bytes = "partial"u8;
            var length = Math.Min(count, bytes.Length);
            bytes[..length].CopyTo(buffer.AsSpan(offset, length));
            return length;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_returnedPartial)
            {
                return ValueTask.FromException<int>(
                    new HttpRequestException("Synthetic mid-body failure."));
            }

            _returnedPartial = true;
            var bytes = "partial"u8;
            var length = Math.Min(buffer.Length, bytes.Length);
            bytes[..length].CopyTo(buffer.Span[..length]);
            return ValueTask.FromResult(length);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class CountingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;
            return Task.FromResult(responseFactory(request));
        }
    }
}
