using FoxData.Sources.WarApi;
using FoxData.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoxData.RecoveryTests;

public sealed class WorkerCancellationTests
{
    [Fact]
    public async Task DisabledBootstrapWorkerStopsCleanly()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();

        var options = new WarApiWorkerOptions(
            Enabled: false,
            Shards: new[] { WarApiShard.Live1 },
            LeaseDuration: TimeSpan.FromMinutes(2),
            IdleDelay: TimeSpan.FromMilliseconds(100),
            RecoveryInterval: TimeSpan.FromSeconds(1),
            PlannerInterval: TimeSpan.FromSeconds(1),
            PlannerBatchSize: 32,
            ConnectTimeout: TimeSpan.FromSeconds(5),
            ExchangeTimeout: TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer: 4,
            MaxResponseHeadersLengthKiB: 16,
            MaxWireBytes: 1024 * 1024,
            MaxDecodedBytes: 2 * 1024 * 1024,
            MaxExpansionRatio: 20,
            OutboundGlobalMinimumInterval: TimeSpan.FromMilliseconds(150),
            OutboundPerHostMinimumInterval: TimeSpan.FromMilliseconds(400),
            UserAgent: "Foxhole-Chronicle/FoxData-Test");

        var worker = new BootstrapWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            NullLogger<BootstrapWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StopAsync(timeout.Token);

        Assert.False(timeout.IsCancellationRequested);
    }
}
