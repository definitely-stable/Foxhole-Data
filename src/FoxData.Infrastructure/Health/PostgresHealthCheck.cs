using FoxData.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FoxData.Infrastructure.Health;

public sealed class PostgresHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<FoxDataDbContext>();

            if (!await dbContext.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy(
                    "PostgreSQL connectivity probe could not connect.");
            }

            var pendingMigration = (await dbContext.Database
                    .GetPendingMigrationsAsync(cancellationToken))
                .FirstOrDefault();

            return pendingMigration is null
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy(
                    $"PostgreSQL schema is behind the application model; pending migration: {pendingMigration}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "PostgreSQL connectivity/schema compatibility probe failed.",
                exception);
        }
    }
}
