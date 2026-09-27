using System.Data;
using FoxData.Application.Canonical;
using FoxData.Core.Runtime;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Canonical;

public sealed class PostgresWarContextReader(NpgsqlDataSource dataSource)
    : IWarContextReader
{
    public async Task<WarContextDescriptor?> GetAtOrBeforeAsync(
        ShardId shardId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        if (shardId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Shard identifier must not be empty.",
                nameof(shardId));
        }

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                war.id,
                war.shard_id,
                war.source_war_id,
                observation.observed_at
            FROM runtime.war_observations AS observation
            INNER JOIN runtime.wars AS war
                ON war.id = observation.war_id
            WHERE war.shard_id = @shard_id
              AND observation.observed_at <= @observed_at
            ORDER BY
                observation.observed_at DESC,
                observation.recorded_at DESC,
                observation.id DESC
            LIMIT 1;
            """;

        command.Parameters.Add("shard_id", NpgsqlDbType.Uuid).Value =
            shardId.Value;
        command.Parameters.Add("observed_at", NpgsqlDbType.TimestampTz).Value =
            observedAt;

        await using var result = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await result.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WarContextDescriptor(
            new WarId(result.GetGuid(0)),
            new ShardId(result.GetGuid(1)),
            result.GetString(2),
            result.GetFieldValue<DateTimeOffset>(3));
    }
}
