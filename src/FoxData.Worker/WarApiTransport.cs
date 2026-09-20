using FoxData.Sources.WarApi;

namespace FoxData.Worker;

public sealed class WarApiTransport : IDisposable
{
    public WarApiTransport(
        WarApiWorkerOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var handler = WarApiHttpHandler.Create(
            options.ConnectTimeout,
            options.MaxConnectionsPerServer,
            options.MaxResponseHeadersLengthKiB);

        Client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        Exchange = new WarApiHttpExchange(
            timeProvider,
            options.ExchangeTimeout,
            options.MaxWireBytes);
    }

    public HttpClient Client { get; }

    public WarApiHttpExchange Exchange { get; }

    public void Dispose() => Client.Dispose();
}
