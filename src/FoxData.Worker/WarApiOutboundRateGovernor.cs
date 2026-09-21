namespace FoxData.Worker;

public sealed class WarApiOutboundRateGovernor(
    TimeProvider timeProvider,
    TimeSpan globalMinimumInterval,
    TimeSpan perHostMinimumInterval)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _nextPerHost =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _nextGlobal = DateTimeOffset.MinValue;

    public async ValueTask WaitAsync(
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestUri);

        DateTimeOffset reservedAt;
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            var host = requestUri.IdnHost;

            var globalEligible = _nextGlobal > now
                ? _nextGlobal
                : now;
            var hostEligible =
                _nextPerHost.TryGetValue(host, out var existing) &&
                existing > now
                    ? existing
                    : now;

            reservedAt = globalEligible > hostEligible
                ? globalEligible
                : hostEligible;

            _nextGlobal = reservedAt + globalMinimumInterval;
            _nextPerHost[host] =
                reservedAt + perHostMinimumInterval;
        }

        var delay = reservedAt - timeProvider.GetUtcNow();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(
                delay,
                timeProvider,
                cancellationToken);
        }
    }
}
