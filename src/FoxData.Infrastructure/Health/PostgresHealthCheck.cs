using FoxData.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

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

            var pending = (await dbContext.Database
                    .GetPendingMigrationsAsync(cancellationToken))
                .Take(8)
                .ToArray();

            if (pending.Length > 0)
            {
                return HealthCheckResult.Unhealthy(
                    "PostgreSQL is reachable but the FoxData schema has pending migrations.",
                    data: new Dictionary<string, object>
                    {
                        ["pendingMigrationCountAtLeast"] = pending.Length,
                    });
            }

            return HealthCheckResult.Healthy("PostgreSQL schema is current for this service build.");
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy(
                "PostgreSQL connectivity/schema readiness probe failed.",
                exception);
        }
    }
}
