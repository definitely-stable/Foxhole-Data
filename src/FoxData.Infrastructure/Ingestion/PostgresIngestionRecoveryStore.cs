using System.Data;
using FoxData.Application.Ingestion;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Ingestion;

public sealed class PostgresIngestionRecoveryStore(NpgsqlDataSource dataSource) : IIngestionRecoveryStore
{
    public async Task<RecoveryBatchResult> RecoverExpiredAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var candidates = await ClaimExpiredCandidatesAsync(
            connection,
            transaction,
            batchSize,
            cancellationToken);

        var results = new List<RecoveryItemResult>(candidates.Count);

        foreach (var candidate in candidates)
        {
            results.Add(await RecoverCandidateAsync(
                connection,
                transaction,
                candidate,
                cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);

        return new RecoveryBatchResult(results);
    }

    private static async Task<IReadOnlyList<RecoveryCandidate>> ClaimExpiredCandidatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, endpoint_id, lease_generation
            FROM ingest.collection_jobs
            WHERE state IN ('leased', 'processing')
              AND lease_expires_at <= transaction_timestamp()
            ORDER BY lease_expires_at, id
            FOR UPDATE SKIP LOCKED
            LIMIT @batch_size;
            """;
        command.Parameters.Add("batch_size", NpgsqlDbType.Integer).Value = batchSize;

        var candidates = new List<RecoveryCandidate>(batchSize);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new RecoveryCandidate(
                new CollectionJobId(reader.GetGuid(0)),
                new EndpointId(reader.GetGuid(1)),
                new LeaseGeneration(reader.GetInt64(2))));
        }

        return candidates;
    }

    private static async Task<RecoveryItemResult> RecoverCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RecoveryCandidate candidate,
        CancellationToken cancellationToken)
    {
        var attempt = await GetLatestAttemptForUpdateAsync(
            connection,
            transaction,
            candidate.JobId,
            cancellationToken);

        if (attempt is null)
        {
            await RequeueJobAsync(
                connection,
                transaction,
                candidate.JobId,
                candidate.LeaseGeneration,
                cancellationToken);

            return new RecoveryItemResult(
                candidate.JobId,
                AttemptId: null,
                RecoveryDisposition.RequeuedNoAttempt);
        }

        var fetchCreatedAt = await GetFetchCreatedAtAsync(
            connection,
            transaction,
            attempt.Id,
            cancellationToken);

        if (fetchCreatedAt is not null)
        {
            await RepairFromFetchAsync(
                connection,
                transaction,
                candidate,
                attempt,
                fetchCreatedAt.Value,
                cancellationToken);

            return new RecoveryItemResult(
                candidate.JobId,
                attempt.Id,
                RecoveryDisposition.RepairedFromFetch);
        }

        if (attempt.ExchangeAuthorizedAt is null)
        {
            await MarkBeforeExchangeFailedAsync(
                connection,
                transaction,
                attempt.Id,
                cancellationToken);
            await ClearEndpointActiveAttemptAsync(
                connection,
                transaction,
                candidate.EndpointId,
                attempt.Id,
                cancellationToken);
            await RequeueJobAsync(
                connection,
                transaction,
                candidate.JobId,
                candidate.LeaseGeneration,
                cancellationToken);

            return new RecoveryItemResult(
                candidate.JobId,
                attempt.Id,
                RecoveryDisposition.RequeuedBeforeExchange);
        }

        await MarkUncertainAsync(
            connection,
            transaction,
            attempt.Id,
            cancellationToken);
        await ClearEndpointActiveAttemptAsync(
            connection,
            transaction,
            candidate.EndpointId,
            attempt.Id,
            cancellationToken);
        await RequeueJobAsync(
            connection,
            transaction,
            candidate.JobId,
            candidate.LeaseGeneration,
            cancellationToken);

        return new RecoveryItemResult(
            candidate.JobId,
            attempt.Id,
            RecoveryDisposition.MarkedUncertain);
    }

    private static async Task<AttemptRecoveryContext?> GetLatestAttemptForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CollectionJobId jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, state, exchange_authorized_at
            FROM ingest.attempts
            WHERE job_id = @job_id
            ORDER BY attempt_number DESC
            LIMIT 1
            FOR UPDATE;
            """;
        AddUuid(command, "job_id", jobId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AttemptRecoveryContext(
            new IngestionAttemptId(reader.GetGuid(0)),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
    }

    private static async Task<DateTimeOffset?> GetFetchCreatedAtAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionAttemptId attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT created_at
            FROM evidence.fetches
            WHERE attempt_id = @attempt_id;
            """;
        AddUuid(command, "attempt_id", attemptId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private static async Task RequeueJobAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CollectionJobId jobId,
        LeaseGeneration leaseGeneration,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE ingest.collection_jobs
            SET state = 'pending',
                lease_owner_id = NULL,
                lease_expires_at = NULL,
                updated_at = transaction_timestamp()
            WHERE id = @job_id
              AND lease_generation = @lease_generation
              AND state IN ('leased', 'processing');
            """;
        AddUuid(command, "job_id", jobId.Value);
        AddBigint(command, "lease_generation", leaseGeneration.Value);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"Expired job {jobId} changed while held by the recovery transaction.");
        }
    }

    private static async Task MarkBeforeExchangeFailedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionAttemptId attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE ingest.attempts
            SET state = 'failed',
                outcome_code = 'abandoned_before_exchange',
                recovered_at = COALESCE(recovered_at, transaction_timestamp()),
                completed_at = COALESCE(completed_at, transaction_timestamp()),
                error_class = COALESCE(error_class, 'recovery'),
                error_code = COALESCE(error_code, 'abandoned_before_exchange'),
                updated_at = transaction_timestamp()
            WHERE id = @attempt_id;
            """;
        AddUuid(command, "attempt_id", attemptId.Value);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"Attempt {attemptId} disappeared during recovery.");
        }
    }

    private static async Task MarkUncertainAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionAttemptId attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE ingest.attempts
            SET state = 'uncertain',
                outcome_code = 'uncertain_exchange',
                recovered_at = COALESCE(recovered_at, transaction_timestamp()),
                completed_at = COALESCE(completed_at, transaction_timestamp()),
                error_class = COALESCE(error_class, 'recovery'),
                error_code = COALESCE(error_code, 'uncertain_exchange'),
                updated_at = transaction_timestamp()
            WHERE id = @attempt_id;
            """;
        AddUuid(command, "attempt_id", attemptId.Value);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"Attempt {attemptId} disappeared during recovery.");
        }
    }

    private static async Task RepairFromFetchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RecoveryCandidate candidate,
        AttemptRecoveryContext attempt,
        DateTimeOffset fetchCreatedAt,
        CancellationToken cancellationToken)
    {
        await using (var attemptCommand = connection.CreateCommand())
        {
            attemptCommand.Transaction = transaction;
            attemptCommand.CommandText =
                """
                UPDATE ingest.attempts
                SET state = CASE
                        WHEN state IN ('completed', 'captured_late') THEN state
                        ELSE 'captured_late'
                    END,
                    outcome_code = CASE
                        WHEN state = 'completed' THEN COALESCE(outcome_code, 'captured_current')
                        ELSE COALESCE(outcome_code, 'recovered_fetch')
                    END,
                    raw_durable_at = COALESCE(raw_durable_at, @fetch_created_at),
                    completed_at = COALESCE(completed_at, transaction_timestamp()),
                    recovered_at = COALESCE(recovered_at, transaction_timestamp()),
                    superseded_at = CASE
                        WHEN state = 'completed' THEN superseded_at
                        ELSE COALESCE(superseded_at, transaction_timestamp())
                    END,
                    updated_at = transaction_timestamp()
                WHERE id = @attempt_id;
                """;
            AddTimestamp(attemptCommand, "fetch_created_at", fetchCreatedAt);
            AddUuid(attemptCommand, "attempt_id", attempt.Id.Value);

            if (await attemptCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    $"Attempt {attempt.Id} disappeared during fetch recovery.");
            }
        }

        await using (var jobCommand = connection.CreateCommand())
        {
            jobCommand.Transaction = transaction;
            jobCommand.CommandText =
                """
                UPDATE ingest.collection_jobs
                SET state = 'completed',
                    lease_owner_id = NULL,
                    lease_expires_at = NULL,
                    completed_at = COALESCE(completed_at, transaction_timestamp()),
                    updated_at = transaction_timestamp()
                WHERE id = @job_id
                  AND lease_generation = @lease_generation
                  AND state IN ('leased', 'processing');
                """;
            AddUuid(jobCommand, "job_id", candidate.JobId.Value);
            AddBigint(jobCommand, "lease_generation", candidate.LeaseGeneration.Value);

            if (await jobCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException(
                    $"Expired job {candidate.JobId} changed during fetch recovery.");
            }
        }

        await ClearEndpointActiveAttemptAsync(
            connection,
            transaction,
            candidate.EndpointId,
            attempt.Id,
            cancellationToken);
    }

    private static async Task ClearEndpointActiveAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EndpointId endpointId,
        IngestionAttemptId attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE ingest.endpoint_state
            SET active_attempt_id = NULL,
                updated_at = transaction_timestamp()
            WHERE endpoint_id = @endpoint_id
              AND active_attempt_id = @attempt_id;
            """;
        AddUuid(command, "endpoint_id", endpointId.Value);
        AddUuid(command, "attempt_id", attemptId.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddUuid(NpgsqlCommand command, string name, Guid value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = value;

    private static void AddBigint(NpgsqlCommand command, string name, long value) =>
        command.Parameters.Add(name, NpgsqlDbType.Bigint).Value = value;

    private static void AddTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value = value;

    private sealed record RecoveryCandidate(
        CollectionJobId JobId,
        EndpointId EndpointId,
        LeaseGeneration LeaseGeneration);

    private sealed record AttemptRecoveryContext(
        IngestionAttemptId Id,
        string State,
        DateTimeOffset? ExchangeAuthorizedAt);
}
