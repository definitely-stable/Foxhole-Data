using System.Data;
using System.Runtime.CompilerServices;
using FoxData.Application.Sources;
using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Sources;

public sealed class PostgresSourceMeasurementReader(NpgsqlDataSource dataSource)
    : ISourceMeasurementReader
{
    public async IAsyncEnumerable<SourceMeasurementFetch> ReadFetchesAsync(
        string sourceKey,
        DateTimeOffset startInclusive,
        DateTimeOffset endExclusive,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateWindow(sourceKey, startInclusive, endExclusive);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                captured_fetch.id,
                captured_fetch.attempt_id,
                endpoint.id,
                source.key,
                shard.key,
                shard.environment,
                endpoint.capability_key,
                endpoint.semantic_key,
                captured_fetch.request_started_at,
                captured_fetch.retrieved_at,
                captured_fetch.status_code,
                captured_fetch.duration_ms,
                payload.id,
                payload.created_at,
                encode(payload.sha256, 'hex'),
                payload.byte_length,
                captured_fetch.source_etag,
                captured_fetch.cache_control,
                captured_fetch.expires_at,
                captured_fetch.source_date,
                captured_fetch.source_age_seconds,
                captured_fetch.retry_after,
                captured_fetch.content_encoding,
                captured_fetch.declared_length,
                captured_fetch.body_error_code
            FROM evidence.fetches AS captured_fetch
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = captured_fetch.endpoint_id
            INNER JOIN sources.shards AS shard
                ON shard.id = endpoint.shard_id
            INNER JOIN sources.sources AS source
                ON source.id = shard.source_id
            LEFT JOIN evidence.payloads AS payload
                ON payload.id = captured_fetch.payload_id
            WHERE source.key = @source_key
              AND captured_fetch.request_started_at >= @start_inclusive
              AND captured_fetch.request_started_at < @end_exclusive
            ORDER BY
                endpoint.id,
                captured_fetch.request_started_at,
                captured_fetch.id;
            """;
        AddWindowParameters(
            command,
            sourceKey,
            startInclusive,
            endExclusive);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess,
            cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            yield return new SourceMeasurementFetch(
                new FetchId(reader.GetGuid(0)),
                new IngestionAttemptId(reader.GetGuid(1)),
                new EndpointId(reader.GetGuid(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.GetInt64(11),
                reader.IsDBNull(12)
                    ? null
                    : new PayloadId(reader.GetGuid(12)),
                reader.IsDBNull(13)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetInt64(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.IsDBNull(18)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(18),
                reader.IsDBNull(19)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(19),
                reader.IsDBNull(20) ? null : reader.GetInt64(20),
                reader.IsDBNull(21) ? null : reader.GetString(21),
                reader.IsDBNull(22) ? null : reader.GetString(22),
                reader.IsDBNull(23) ? null : reader.GetInt64(23),
                reader.IsDBNull(24) ? null : reader.GetString(24));
        }
    }

    public async IAsyncEnumerable<SourceMeasurementAttempt> ReadAttemptsAsync(
        string sourceKey,
        DateTimeOffset startInclusive,
        DateTimeOffset endExclusive,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateWindow(sourceKey, startInclusive, endExclusive);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                attempt.id,
                collection_job.id,
                endpoint.id,
                attempt.attempt_number,
                collection_job.idempotency_key,
                source.key,
                shard.key,
                shard.environment,
                endpoint.capability_key,
                endpoint.semantic_key,
                collection_job.scheduled_for,
                attempt.started_at,
                attempt.exchange_authorized_at,
                attempt.raw_durable_at,
                attempt.completed_at,
                attempt.state,
                attempt.outcome_code,
                attempt.error_class,
                attempt.error_code
            FROM ingest.attempts AS attempt
            INNER JOIN ingest.collection_jobs AS collection_job
                ON collection_job.id = attempt.job_id
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = collection_job.endpoint_id
            INNER JOIN sources.shards AS shard
                ON shard.id = endpoint.shard_id
            INNER JOIN sources.sources AS source
                ON source.id = shard.source_id
            WHERE source.key = @source_key
              AND attempt.started_at >= @start_inclusive
              AND attempt.started_at < @end_exclusive
            ORDER BY
                endpoint.id,
                attempt.started_at,
                attempt.id;
            """;
        AddWindowParameters(
            command,
            sourceKey,
            startInclusive,
            endExclusive);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess,
            cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            yield return new SourceMeasurementAttempt(
                new IngestionAttemptId(reader.GetGuid(0)),
                new CollectionJobId(reader.GetGuid(1)),
                new EndpointId(reader.GetGuid(2)),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetFieldValue<DateTimeOffset>(10),
                reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(12),
                reader.IsDBNull(13)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(13),
                reader.IsDBNull(14)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(14),
                reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.IsDBNull(18) ? null : reader.GetString(18));
        }
    }

    public async IAsyncEnumerable<SourceMeasurementParseRun> ReadParseRunsAsync(
        string sourceKey,
        DateTimeOffset startInclusive,
        DateTimeOffset endExclusive,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateWindow(sourceKey, startInclusive, endExclusive);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                parse_run.id,
                parse_run.representation_fetch_id,
                endpoint.id,
                source.key,
                shard.key,
                shard.environment,
                parse_run.capability_key,
                endpoint.semantic_key,
                parse_run.adapter_version,
                parse_run.parser_version,
                parse_run.fingerprint_algorithm,
                parse_run.structural_fingerprint,
                parse_run.outcome,
                parse_run.unknown_property_count,
                parse_run.unknown_code_count,
                parse_run.error_code,
                representation_fetch.request_started_at,
                parse_run.started_at,
                parse_run.completed_at,
                parse_run.source_version,
                parse_run.source_last_updated,
                parse_run.decoded_byte_length
            FROM evidence.source_parse_runs AS parse_run
            INNER JOIN evidence.fetches AS representation_fetch
                ON representation_fetch.id = parse_run.representation_fetch_id
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = representation_fetch.endpoint_id
            INNER JOIN sources.shards AS shard
                ON shard.id = endpoint.shard_id
            INNER JOIN sources.sources AS source
                ON source.id = shard.source_id
            WHERE source.key = @source_key
              AND representation_fetch.request_started_at >= @start_inclusive
              AND representation_fetch.request_started_at < @end_exclusive
            ORDER BY
                endpoint.id,
                representation_fetch.request_started_at,
                parse_run.id;
            """;
        AddWindowParameters(
            command,
            sourceKey,
            startInclusive,
            endExclusive);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess,
            cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            yield return new SourceMeasurementParseRun(
                new SourceParseRunId(reader.GetGuid(0)),
                new FetchId(reader.GetGuid(1)),
                new EndpointId(reader.GetGuid(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetString(12),
                reader.GetInt32(13),
                reader.GetInt32(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.GetFieldValue<DateTimeOffset>(16),
                reader.GetFieldValue<DateTimeOffset>(17),
                reader.GetFieldValue<DateTimeOffset>(18),
                reader.IsDBNull(19) ? null : reader.GetInt64(19),
                reader.IsDBNull(20) ? null : reader.GetInt64(20),
                reader.IsDBNull(21) ? null : reader.GetInt64(21));
        }
    }

    public async IAsyncEnumerable<SourceMeasurementScheduleDecision> ReadScheduleDecisionsAsync(
        string sourceKey,
        DateTimeOffset startInclusive,
        DateTimeOffset endExclusive,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateWindow(sourceKey, startInclusive, endExclusive);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                decision.fetch_id,
                decision.endpoint_id,
                source.key,
                shard.key,
                shard.environment,
                endpoint.capability_key,
                endpoint.semantic_key,
                decision.policy_version,
                decision.effective_cadence_ms,
                decision.endpoint_active,
                decision.probe_selected,
                decision.source_cache_eligible_at,
                decision.next_target_at,
                decision.retry_eligible_at,
                decision.successor_job_id,
                decision.successor_available_at,
                decision.created_at
            FROM evidence.source_schedule_decisions AS decision
            INNER JOIN evidence.fetches AS captured_fetch
                ON captured_fetch.id = decision.fetch_id
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = decision.endpoint_id
            INNER JOIN sources.shards AS shard
                ON shard.id = endpoint.shard_id
            INNER JOIN sources.sources AS source
                ON source.id = shard.source_id
            WHERE source.key = @source_key
              AND (
                    (
                        captured_fetch.request_started_at >= @start_inclusive
                        AND captured_fetch.request_started_at < @end_exclusive
                    )
                    OR EXISTS (
                        SELECT 1
                        FROM ingest.attempts AS successor_attempt
                        WHERE successor_attempt.job_id = decision.successor_job_id
                          AND successor_attempt.started_at >= @start_inclusive
                          AND successor_attempt.started_at < @end_exclusive
                    )
              )
            ORDER BY
                endpoint.id,
                captured_fetch.request_started_at,
                decision.fetch_id;
            """;
        AddWindowParameters(
            command,
            sourceKey,
            startInclusive,
            endExclusive);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess,
            cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            yield return new SourceMeasurementScheduleDecision(
                new FetchId(reader.GetGuid(0)),
                new EndpointId(reader.GetGuid(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt64(8),
                reader.GetBoolean(9),
                reader.GetBoolean(10),
                reader.IsDBNull(11)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(12),
                reader.IsDBNull(13)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(13),
                reader.IsDBNull(14)
                    ? null
                    : new CollectionJobId(reader.GetGuid(14)),
                reader.IsDBNull(15)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(15),
                reader.GetFieldValue<DateTimeOffset>(16));
        }
    }

    private static void ValidateWindow(
        string sourceKey,
        DateTimeOffset startInclusive,
        DateTimeOffset endExclusive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        if (!string.Equals(
                sourceKey,
                sourceKey.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Source key must already be trimmed.",
                nameof(sourceKey));
        }

        if (startInclusive >= endExclusive)
        {
            throw new ArgumentException(
                "Measurement start must be earlier than the exclusive end.",
                nameof(endExclusive));
        }
    }

    private static void AddWindowParameters(
        NpgsqlCommand command,
        string sourceKey,
        DateTimeOffset startInclusive,
        DateTimeOffset endExclusive)
    {
        command.Parameters.Add("source_key", NpgsqlDbType.Text).Value =
            sourceKey;
        command.Parameters.Add(
            "start_inclusive",
            NpgsqlDbType.TimestampTz).Value = startInclusive;
        command.Parameters.Add(
            "end_exclusive",
            NpgsqlDbType.TimestampTz).Value = endExclusive;
    }
}
