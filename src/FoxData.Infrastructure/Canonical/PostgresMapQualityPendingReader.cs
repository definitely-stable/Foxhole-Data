using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Canonical;

// Narrow M6-E recovery: normalized, body-bearing map snapshots that have
// no current-version quality result. Generalized 304/reprocessing is M6-G.
public sealed class PostgresMapQualityPendingReader(NpgsqlDataSource dataSource)
    : IMapQualityPendingReader
{
    public async Task<IReadOnlyList<MapQualityPendingSnapshot>> GetPendingAsync(
        string sourceKey,
        string taxonomyVersion,
        string qualityPolicyVersion,
        DateTimeOffset? afterRetrievedAt,
        FetchId? afterFetchId,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) ||
            string.IsNullOrWhiteSpace(taxonomyVersion) ||
            string.IsNullOrWhiteSpace(qualityPolicyVersion))
        {
            throw new ArgumentException(
                "Pending quality recovery requires source and version identities.");
        }

        if (batchSize is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        if ((afterRetrievedAt is null) != (afterFetchId is null))
        {
            throw new ArgumentException(
                "Pending quality recovery cursor must contain both timestamp and Fetch ID.");
        }

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                snapshot.normalization_run_id,
                endpoint.shard_id,
                representation.id,
                representation.retrieved_at
            FROM evidence.map_snapshots AS snapshot
            JOIN evidence.normalization_runs AS normalization
                ON normalization.id = snapshot.normalization_run_id
            JOIN evidence.fetches AS representation
                ON representation.id = snapshot.representation_fetch_id
            JOIN ingest.attempts AS attempt
                ON attempt.id = representation.attempt_id
            JOIN sources.endpoints AS endpoint
                ON endpoint.id = representation.endpoint_id
            JOIN sources.shards AS shard
                ON shard.id = endpoint.shard_id
            JOIN sources.sources AS source
                ON source.id = shard.source_id
            WHERE source.key = @source_key
              AND normalization.outcome = 'normalized'
              AND representation.status_code = 200
              AND representation.payload_id IS NOT NULL
              AND attempt.outcome_code = 'captured_current'
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM quality.map_quality_runs AS run
                  WHERE run.map_snapshot_id = snapshot.id
                    AND run.validation_fetch_id = representation.id
                    AND run.taxonomy_version = @taxonomy_version
                    AND run.quality_policy_version = @policy_version
              )
              AND (
                  @after_at IS NULL
                  OR (representation.retrieved_at, representation.id)
                        > (@after_at, @after_id)
              )
            ORDER BY representation.retrieved_at, representation.id
            LIMIT @batch_size;
            """;
        command.Parameters.Add("source_key", NpgsqlDbType.Text)
            .Value = sourceKey;
        command.Parameters.Add("taxonomy_version", NpgsqlDbType.Text)
            .Value = taxonomyVersion;
        command.Parameters.Add("policy_version", NpgsqlDbType.Text)
            .Value = qualityPolicyVersion;
        command.Parameters.Add("after_at", NpgsqlDbType.TimestampTz)
            .Value = (object?)afterRetrievedAt ?? DBNull.Value;
        command.Parameters.Add("after_id", NpgsqlDbType.Uuid)
            .Value = (object?)afterFetchId?.Value ?? DBNull.Value;
        command.Parameters.Add("batch_size", NpgsqlDbType.Integer)
            .Value = batchSize;

        var pending = new List<MapQualityPendingSnapshot>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pending.Add(
                new MapQualityPendingSnapshot(
                    new NormalizationRunId(reader.GetGuid(0)),
                    new ShardId(reader.GetGuid(1)),
                    new FetchId(reader.GetGuid(2)),
                    reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return pending;
    }
}
