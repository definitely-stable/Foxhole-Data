using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace FoxData.Infrastructure.Health;

public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";

            var result = await command.ExecuteScalarAsync(cancellationToken);

            return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) == 1
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("PostgreSQL connectivity probe returned an unexpected value.");
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL connectivity probe failed.", exception);
        }
    }
}
