using FoxData.Sources.WarApi;

namespace FoxData.Worker;

public interface IWarApiTransport
{
    Task<WarApiHttpExchangeResult> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken);
}

public sealed class WarApiTransport : IWarApiTransport, IDisposable
{
    private readonly WarApiOutboundRateGovernor _rateGovernor;

    public WarApiTransport(
        WarApiWorkerOptions options,
        TimeProvider timeProvider,
        WarApiOutboundRateGovernor rateGovernor)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(rateGovernor);

        _rateGovernor = rateGovernor;

        var handler = WarApiHttpHandler.Create(
            options.ConnectTimeout,
            options.MaxConnectionsPerServer,
            options.MaxResponseHeadersLengthKiB);

        Client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        Client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            options.UserAgent);

        Exchange = new WarApiHttpExchange(
            timeProvider,
            options.ExchangeTimeout,
            options.MaxWireBytes);
    }

    public HttpClient Client { get; }

    public WarApiHttpExchange Exchange { get; }

    public async Task<WarApiHttpExchangeResult> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is null)
        {
            throw new InvalidOperationException(
                "War API request URI must be set before transport execution.");
        }

        await _rateGovernor.WaitAsync(
            request.RequestUri,
            cancellationToken);

        return await Exchange.SendAsync(
            Client,
            request,
            cancellationToken);
    }

    public void Dispose() => Client.Dispose();
}
