using FoxData.Application.Sources;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Sources;

public sealed class PostgresSourcePlanningReader(NpgsqlDataSource dataSource)
    : ISourcePlanningReader
{
    public async Task<IReadOnlyList<EndpointId>> ListPendingCurrentEndpointsAsync(
        string sourceKey,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        if (maximumCount is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount),
                maximumCount,
                "Maximum count must be between 1 and 4096.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT endpoint.id
            FROM sources.endpoints AS endpoint
            INNER JOIN sources.shards AS shard
                ON shard.id = endpoint.shard_id
            INNER JOIN sources.sources AS source
                ON source.id = shard.source_id
            INNER JOIN ingest.endpoint_state AS capture_state
                ON capture_state.endpoint_id = endpoint.id
            INNER JOIN evidence.fetches AS current_fetch
                ON current_fetch.attempt_id = capture_state.last_current_capture_attempt_id
            LEFT JOIN ingest.endpoint_poll_state AS poll_state
                ON poll_state.endpoint_id = endpoint.id
            WHERE source.key = @source_key
              AND source.enabled
              AND shard.enabled
              AND endpoint.enabled
              AND (
                    poll_state.last_processed_fetch_id IS NULL
                    OR poll_state.last_processed_fetch_id <> current_fetch.id
                  )
            ORDER BY current_fetch.retrieved_at, current_fetch.id
            LIMIT @maximum_count;
            """;
        command.Parameters.Add("source_key", NpgsqlDbType.Text).Value = sourceKey;
        command.Parameters.Add("maximum_count", NpgsqlDbType.Integer).Value = maximumCount;

        var result = new List<EndpointId>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new EndpointId(reader.GetGuid(0)));
        }

        return result;
    }
}
