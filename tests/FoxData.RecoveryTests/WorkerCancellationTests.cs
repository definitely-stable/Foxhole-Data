using FoxData.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoxData.RecoveryTests;

public sealed class WorkerCancellationTests
{
    [Fact]
    public async Task BootstrapWorkerStopsWhenCancelled()
    {
        var worker = new BootstrapWorker(NullLogger<BootstrapWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StopAsync(timeout.Token);

        Assert.False(timeout.IsCancellationRequested);
    }
}
