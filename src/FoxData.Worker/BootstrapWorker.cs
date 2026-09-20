namespace FoxData.Worker;

public sealed class BootstrapWorker(ILogger<BootstrapWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Foxhole-Data worker bootstrap is running; source ingestion is not implemented in M1.");

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during host shutdown.
        }
    }
}
