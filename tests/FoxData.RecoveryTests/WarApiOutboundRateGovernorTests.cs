using System.Diagnostics;
using FoxData.Worker;

namespace FoxData.RecoveryTests;

public sealed class WarApiOutboundRateGovernorTests
{
    [Fact]
    public async Task GovernorSpacesRequestsGloballyAndPerHost()
    {
        var governor = new WarApiOutboundRateGovernor(
            TimeProvider.System,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(60));

        var firstHost = new Uri("https://war-service-live.foxholeservices.com/api/");
        var secondHost = new Uri("https://war-service-live-2.foxholeservices.com/api/");

        var stopwatch = Stopwatch.StartNew();

        await governor.WaitAsync(
            firstHost,
            TestContext.Current.CancellationToken);
        await governor.WaitAsync(
            secondHost,
            TestContext.Current.CancellationToken);
        await governor.WaitAsync(
            firstHost,
            TestContext.Current.CancellationToken);

        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(45),
            $"Expected rate governor spacing, observed {stopwatch.Elapsed.TotalMilliseconds:0.##} ms.");
    }

    [Fact]
    public async Task GovernorDoesNotBurstConcurrentReservations()
    {
        var governor = new WarApiOutboundRateGovernor(
            TimeProvider.System,
            TimeSpan.FromMilliseconds(15),
            TimeSpan.FromMilliseconds(40));

        var host = new Uri("https://war-service-live.foxholeservices.com/api/");
        var stopwatch = Stopwatch.StartNew();

        await Task.WhenAll(
            governor.WaitAsync(
                host,
                TestContext.Current.CancellationToken).AsTask(),
            governor.WaitAsync(
                host,
                TestContext.Current.CancellationToken).AsTask(),
            governor.WaitAsync(
                host,
                TestContext.Current.CancellationToken).AsTask());

        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(70),
            $"Expected serialized per-host reservations, observed {stopwatch.Elapsed.TotalMilliseconds:0.##} ms.");
    }
}
