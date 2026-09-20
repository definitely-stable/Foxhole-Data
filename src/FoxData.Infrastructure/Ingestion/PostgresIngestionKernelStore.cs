using System.Data;
using FoxData.Application.Ingestion;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Ingestion;

public sealed class PostgresIngestionKernelStore(NpgsqlDataSource dataSource) : IIngestionKernelStore
{
    private const string JobColumns =
        """
        id, endpoint_id, idempotency_key, scheduled_for, available_at, priority, state,
        lease_owner_id, lease_generation, lease_expires_at, attempt_count,
        created_at, updated_at, completed_at
        """;

    private const string AttemptColumns =
        """
        id, job_id, attempt_number, lease_generation, fence_token, state, outcome_code,
        started_at, exchange_authorized_at, raw_durable_at, completed_at, recovered_at,
        superseded_at, error_class, error_code, created_at, updated_at
        """;

    public async Task<JobEnqueueResult> EnqueueAsync(
        CollectionJobId proposedId,
        EndpointId endpointId,
        string idempotencyKey,
        DateTimeOffset scheduledFor,
        DateTimeOffset availableAt,
        short priority,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            $"""
            INSERT INTO ingest.collection_jobs
                (id, endpoint_id, idempotency_key, scheduled_for, available_at, priority, state)
            VALUES
                (@id, @endpoint_id, @idempotency_key, @scheduled_for, @available_at, @priority, 'pending')
            ON CONFLICT (endpoint_id, idempotency_key) DO NOTHING
            RETURNING {JobColumns};
            """;

        AddUuid(insert, "id", proposedId.Value);
        AddUuid(insert, "endpoint_id", endpointId.Value);
        AddText(insert, "idempotency_key", idempotencyKey);
        AddTimestamp(insert, "scheduled_for", scheduledFor);
        AddTimestamp(insert, "available_at", availableAt);
        insert.Parameters.Add("priority", NpgsqlDbType.Smallint).Value = priority;

        var created = await ReadJobAsync(insert, cancellationToken);
        if (created is not null)
        {
            return new JobEnqueueResult(JobEnqueueStatus.Created, created);
        }

        var existing = await GetJobByIdempotencyKeyAsync(
                connection,
                endpointId,
                idempotencyKey,
                cancellationToken)
            ?? throw new InvalidOperationException(
                "Job uniqueness conflict was observed but the existing row was not readable.");

        var compatible =
            existing.ScheduledFor == scheduledFor &&
            existing.AvailableAt == availableAt &&
            existing.Priority == priority;

        return new JobEnqueueResult(
            compatible ? JobEnqueueStatus.Existing : JobEnqueueStatus.Conflict,
            existing);
    }

    public async Task<JobClaimResult> ClaimNextAsync(
        WorkerInstanceId workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            WITH candidate AS (
                SELECT id
                FROM ingest.collection_jobs
                WHERE state = 'pending'
                  AND available_at <= clock_timestamp()
                ORDER BY available_at, priority DESC, id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE ingest.collection_jobs AS job
            SET state = 'leased',
                lease_owner_id = @worker_id,
                lease_generation = job.lease_generation + 1,
                lease_expires_at = clock_timestamp() + @lease_duration,
                updated_at = transaction_timestamp()
            FROM candidate
            WHERE job.id = candidate.id
            RETURNING {PrefixColumns(JobColumns, "job")};
            """;

        AddUuid(command, "worker_id", workerId.Value);
        AddInterval(command, "lease_duration", leaseDuration);

        var claimed = await ReadJobAsync(command, cancellationToken);
        return claimed is null ? JobClaimResult.None : new JobClaimResult(claimed);
    }

    public async Task<LeaseRenewalResult> RenewLeaseAsync(
        CollectionJobId jobId,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var current = await GetJobForUpdateAsync(
            connection,
            transaction,
            jobId,
            cancellationToken);

        if (!await IsCurrentLeaseOwnerAsync(
                connection,
                transaction,
                current,
                workerId,
                leaseGeneration,
                allowProcessing: true,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new LeaseRenewalResult(LeaseRenewalStatus.Lost, current);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            UPDATE ingest.collection_jobs
            SET lease_expires_at = clock_timestamp() + @lease_duration,
                updated_at = transaction_timestamp()
            WHERE id = @job_id
              AND lease_owner_id = @worker_id
              AND lease_generation = @lease_generation
              AND state IN ('leased', 'processing')
            RETURNING {JobColumns};
            """;

        AddUuid(command, "job_id", jobId.Value);
        AddUuid(command, "worker_id", workerId.Value);
        AddBigint(command, "lease_generation", leaseGeneration.Value);
        AddInterval(command, "lease_duration", leaseDuration);

        var renewed = await ReadJobAsync(command, cancellationToken)
            ?? throw new InvalidOperationException(
                "Locked lease renewal did not return the collection job.");

        await transaction.CommitAsync(cancellationToken);

        return new LeaseRenewalResult(LeaseRenewalStatus.Renewed, renewed);
    }

    public async Task<LeaseReleaseResult> ReleaseLeaseAsync(
        CollectionJobId jobId,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var current = await GetJobForUpdateAsync(
            connection,
            transaction,
            jobId,
            cancellationToken);

        if (current is null ||
            current.LeaseOwnerId != workerId ||
            current.LeaseGeneration != leaseGeneration ||
            current.LeaseExpiresAt is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new LeaseReleaseResult(LeaseReleaseStatus.Lost, current);
        }

        if (current.State is not CollectionJobState.Leased)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new LeaseReleaseResult(LeaseReleaseStatus.InvalidState, current);
        }

        var databaseNow = await GetDatabaseNowAsync(
            connection,
            transaction,
            cancellationToken);

        if (current.LeaseExpiresAt <= databaseNow)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new LeaseReleaseResult(LeaseReleaseStatus.Lost, current);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            UPDATE ingest.collection_jobs
            SET state = 'pending',
                lease_owner_id = NULL,
                lease_expires_at = NULL,
                updated_at = transaction_timestamp()
            WHERE id = @job_id
              AND lease_owner_id = @worker_id
              AND lease_generation = @lease_generation
              AND state = 'leased'
            RETURNING {JobColumns};
            """;

        AddUuid(command, "job_id", jobId.Value);
        AddUuid(command, "worker_id", workerId.Value);
        AddBigint(command, "lease_generation", leaseGeneration.Value);

        var released = await ReadJobAsync(command, cancellationToken)
            ?? throw new InvalidOperationException(
                "Locked lease release did not return the collection job.");

        await transaction.CommitAsync(cancellationToken);

        return new LeaseReleaseResult(LeaseReleaseStatus.Released, released);
    }

    public async Task<BeginAttemptResult> BeginAttemptAsync(
        IngestionAttemptId attemptId,
        CollectionJobId jobId,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var existing = await GetAttemptAsync(
            connection,
            transaction,
            attemptId,
            forUpdate: false,
            cancellationToken);

        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);

            return existing.JobId == jobId
                ? new BeginAttemptResult(BeginAttemptStatus.Existing, existing)
                : throw new InvalidOperationException(
                    "Attempt identifier already exists for a different job.");
        }

        var ownedJob = await GetOwnedJobForUpdateAsync(
            connection,
            transaction,
            jobId,
            workerId,
            leaseGeneration,
            cancellationToken);

        if (ownedJob is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new BeginAttemptResult(BeginAttemptStatus.LeaseLost, null);
        }

        existing = await GetAttemptAsync(
            connection,
            transaction,
            attemptId,
            forUpdate: false,
            cancellationToken);

        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);

            return existing.JobId == jobId
                ? new BeginAttemptResult(BeginAttemptStatus.Existing, existing)
                : throw new InvalidOperationException(
                    "Attempt identifier already exists for a different job.");
        }

        await using (var active = connection.CreateCommand())
        {
            active.Transaction = transaction;
            active.CommandText =
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM ingest.attempts
                    WHERE job_id = @job_id
                      AND state IN ('created', 'fenced', 'exchange_authorized', 'raw_durable')
                );
                """;
            AddUuid(active, "job_id", jobId.Value);

            if (await active.ExecuteScalarAsync(cancellationToken) is true)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new BeginAttemptResult(BeginAttemptStatus.ActiveAttemptExists, null);
            }
        }

        int attemptNumber;

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE ingest.collection_jobs
                SET attempt_count = attempt_count + 1,
                    state = 'processing',
                    updated_at = transaction_timestamp()
                WHERE id = @job_id
                RETURNING attempt_count;
                """;
            AddUuid(update, "job_id", jobId.Value);

            attemptNumber = Convert.ToInt32(
                await update.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                $"""
                INSERT INTO ingest.attempts
                    (id, job_id, attempt_number, lease_generation, state, started_at)
                VALUES
                    (@id, @job_id, @attempt_number, @lease_generation, 'created', transaction_timestamp())
                RETURNING {AttemptColumns};
                """;

            AddUuid(insert, "id", attemptId.Value);
            AddUuid(insert, "job_id", jobId.Value);
            insert.Parameters.Add("attempt_number", NpgsqlDbType.Integer).Value = attemptNumber;
            AddBigint(insert, "lease_generation", leaseGeneration.Value);

            var started = await ReadAttemptAsync(insert, cancellationToken)
                ?? throw new InvalidOperationException("Attempt insert returned no row.");

            await transaction.CommitAsync(cancellationToken);
            return new BeginAttemptResult(BeginAttemptStatus.Started, started);
        }
    }

    public async Task<FenceAcquireResult> AcquireEndpointFenceAsync(
        IngestionAttemptId attemptId,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var attemptSnapshot = await GetAttemptAsync(
            connection,
            transaction,
            attemptId,
            forUpdate: false,
            cancellationToken);

        if (attemptSnapshot is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new FenceAcquireResult(FenceAcquireStatus.InvalidAttempt, null);
        }

        var ownedJob = await GetOwnedJobForUpdateAsync(
            connection,
            transaction,
            attemptSnapshot.JobId,
            workerId,
            leaseGeneration,
            cancellationToken);

        if (ownedJob is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new FenceAcquireResult(FenceAcquireStatus.LeaseLost, attemptSnapshot);
        }

        var attempt = await GetAttemptAsync(
            connection,
            transaction,
            attemptId,
            forUpdate: true,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Attempt disappeared after its collection job was locked.");

        if (attempt.JobId != ownedJob.Id ||
            attempt.LeaseGeneration != leaseGeneration)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new FenceAcquireResult(FenceAcquireStatus.LeaseLost, attempt);
        }

        var endpointState = await GetEndpointStateForUpdateAsync(
            connection,
            transaction,
            ownedJob.EndpointId,
            cancellationToken);

        if (endpointState is null)
        {
            throw new InvalidOperationException(
                $"Endpoint state is missing for endpoint {ownedJob.EndpointId}.");
        }

        if (attempt.FenceToken is not null)
        {
            await transaction.CommitAsync(cancellationToken);

            var stillCurrent =
                endpointState.ActiveAttemptId == attempt.Id &&
                endpointState.FenceToken == attempt.FenceToken.Value;

            return new FenceAcquireResult(
                stillCurrent ? FenceAcquireStatus.AlreadyAcquired : FenceAcquireStatus.StaleFence,
                attempt);
        }

        if (attempt.State is not IngestionAttemptState.Created)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new FenceAcquireResult(FenceAcquireStatus.InvalidState, attempt);
        }

        FenceToken token;

        await using (var fence = connection.CreateCommand())
        {
            fence.Transaction = transaction;
            fence.CommandText =
                """
                UPDATE ingest.endpoint_state
                SET fence_token = fence_token + 1,
                    active_attempt_id = @attempt_id,
                    updated_at = transaction_timestamp()
                WHERE endpoint_id = @endpoint_id
                RETURNING fence_token;
                """;
            AddUuid(fence, "attempt_id", attemptId.Value);
            AddUuid(fence, "endpoint_id", ownedJob.EndpointId.Value);

            var value = await fence.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Endpoint fence update returned no token.");

            token = new FenceToken(Convert.ToInt64(
                value,
                System.Globalization.CultureInfo.InvariantCulture));
        }

        await using (var updateAttempt = connection.CreateCommand())
        {
            updateAttempt.Transaction = transaction;
            updateAttempt.CommandText =
                """
                UPDATE ingest.attempts
                SET fence_token = @fence_token,
                    state = 'fenced',
                    updated_at = transaction_timestamp()
                WHERE id = @attempt_id;
                """;
            AddBigint(updateAttempt, "fence_token", token.Value);
            AddUuid(updateAttempt, "attempt_id", attemptId.Value);

            var changed = await updateAttempt.ExecuteNonQueryAsync(cancellationToken);
            if (changed != 1)
            {
                throw new InvalidOperationException("Attempt fence update did not affect exactly one row.");
            }
        }

        var fencedAttempt = await GetAttemptAsync(
            connection,
            transaction,
            attemptId,
            forUpdate: false,
            cancellationToken)
            ?? throw new InvalidOperationException("Fenced attempt was not readable.");

        await transaction.CommitAsync(cancellationToken);

        return new FenceAcquireResult(FenceAcquireStatus.AcquiredNow, fencedAttempt);
    }

    public async Task<ExchangeAuthorizationResult> AuthorizeExchangeAsync(
        IngestionAttemptId attemptId,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var attemptSnapshot = await GetAttemptAsync(
            connection,
            transaction,
            attemptId,
            forUpdate: false,
            cancellationToken);

        if (attemptSnapshot is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ExchangeAuthorizationResult(
                ExchangeAuthorizationStatus.InvalidAttempt,
                null);
        }

        if (attemptSnapshot.ExchangeAuthorizedAt is not null ||
            attemptSnapshot.State is IngestionAttemptState.ExchangeAuthorized)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ExchangeAuthorizationResult(
                ExchangeAuthorizationStatus.AlreadyAuthorized,
                attemptSnapshot);
        }

        if (attemptSnapshot.State is not IngestionAttemptState.Fenced ||
            attemptSnapshot.FenceToken is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ExchangeAuthorizationResult(
                ExchangeAuthorizationStatus.InvalidState,
                attemptSnapshot);
        }

        var ownedJob = await GetOwnedJobForUpdateAsync(
            connection,
            transaction,
            attemptSnapshot.JobId,
            workerId,
            leaseGeneration,
            cancellationToken);

        if (ownedJob is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ExchangeAuthorizationResult(
                ExchangeAuthorizationStatus.LeaseLost,
                attemptSnapshot);
        }

        var attempt = await GetAttemptAsync(
            connection,
            transaction,
            attemptId,
            forUpdate: true,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Attempt disappeared after its collection job was locked.");

        if (attempt.ExchangeAuthorizedAt is not null ||
            attempt.State is IngestionAttemptState.ExchangeAuthorized)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ExchangeAuthorizationResult(
                ExchangeAuthorizationStatus.AlreadyAuthorized,
                attempt);
        }

        if (attempt.JobId != ownedJob.Id ||
            attempt.LeaseGeneration != leaseGeneration)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ExchangeAuthorizationResult(
                ExchangeAuthorizationStatus.LeaseLost,
                attempt);
        }

        if (attempt.State is not IngestionAttemptState.Fenced ||
            attempt.FenceToken is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ExchangeAuthorizationResult(
                ExchangeAuthorizationStatus.InvalidState,
                attempt);
        }

        var endpointState = await GetEndpointStateForUpdateAsync(
            connection,
            transaction,
            ownedJob.EndpointId,
            cancellationToken);

        var currentFence =
            endpointState is not null &&
            endpointState.ActiveAttemptId == attempt.Id &&
            endpointState.FenceToken == attempt.FenceToken.Value;

        if (!currentFence)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ExchangeAuthorizationResult(
                ExchangeAuthorizationStatus.StaleFence,
                attempt);
        }

        await using (var authorizeCommand = connection.CreateCommand())
        {
            authorizeCommand.Transaction = transaction;
            authorizeCommand.CommandText =
                """
                UPDATE ingest.attempts
                SET state = 'exchange_authorized',
                    exchange_authorized_at = transaction_timestamp(),
                    updated_at = transaction_timestamp()
                WHERE id = @attempt_id;
                """;
            AddUuid(authorizeCommand, "attempt_id", attemptId.Value);

            var changed = await authorizeCommand.ExecuteNonQueryAsync(cancellationToken);
            if (changed != 1)
            {
                throw new InvalidOperationException(
                    "Exchange authorization did not affect exactly one attempt.");
            }
        }

        var authorized = await GetAttemptAsync(
            connection,
            transaction,
            attemptId,
            forUpdate: false,
            cancellationToken)
            ?? throw new InvalidOperationException("Authorized attempt was not readable.");

        await transaction.CommitAsync(cancellationToken);

        return new ExchangeAuthorizationResult(
            ExchangeAuthorizationStatus.AuthorizedNow,
            authorized);
    }

    public async Task<CollectionJobDescriptor?> GetJobAsync(
        CollectionJobId jobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await GetJobAsync(connection, jobId, cancellationToken);
    }

    public async Task<IngestionAttemptDescriptor?> GetAttemptAsync(
        IngestionAttemptId attemptId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await GetAttemptAsync(
            connection,
            transaction: null,
            attemptId,
            forUpdate: false,
            cancellationToken);
    }

    private static async Task<CollectionJobDescriptor?> GetJobAsync(
        NpgsqlConnection connection,
        CollectionJobId jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {JobColumns}
            FROM ingest.collection_jobs
            WHERE id = @job_id;
            """;
        AddUuid(command, "job_id", jobId.Value);

        return await ReadJobAsync(command, cancellationToken);
    }

    private static async Task<CollectionJobDescriptor?> GetJobByIdempotencyKeyAsync(
        NpgsqlConnection connection,
        EndpointId endpointId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {JobColumns}
            FROM ingest.collection_jobs
            WHERE endpoint_id = @endpoint_id
              AND idempotency_key = @idempotency_key;
            """;
        AddUuid(command, "endpoint_id", endpointId.Value);
        AddText(command, "idempotency_key", idempotencyKey);

        return await ReadJobAsync(command, cancellationToken);
    }

    private static async Task<CollectionJobDescriptor?> GetOwnedJobForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CollectionJobId jobId,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        CancellationToken cancellationToken)
    {
        var current = await GetJobForUpdateAsync(
            connection,
            transaction,
            jobId,
            cancellationToken);

        return await IsCurrentLeaseOwnerAsync(
                connection,
                transaction,
                current,
                workerId,
                leaseGeneration,
                allowProcessing: true,
                cancellationToken)
            ? current
            : null;
    }

    private static async Task<CollectionJobDescriptor?> GetJobForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CollectionJobId jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {JobColumns}
            FROM ingest.collection_jobs
            WHERE id = @job_id
            FOR UPDATE;
            """;
        AddUuid(command, "job_id", jobId.Value);

        return await ReadJobAsync(command, cancellationToken);
    }

    private static async Task<bool> IsCurrentLeaseOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CollectionJobDescriptor? current,
        WorkerInstanceId workerId,
        LeaseGeneration leaseGeneration,
        bool allowProcessing,
        CancellationToken cancellationToken)
    {
        if (current is null ||
            current.LeaseOwnerId != workerId ||
            current.LeaseGeneration != leaseGeneration ||
            current.LeaseExpiresAt is null ||
            current.State is not CollectionJobState.Leased &&
            !(allowProcessing && current.State is CollectionJobState.Processing))
        {
            return false;
        }

        var databaseNow = await GetDatabaseNowAsync(
            connection,
            transaction,
            cancellationToken);

        return current.LeaseExpiresAt > databaseNow;
    }

    private static async Task<DateTimeOffset> GetDatabaseNowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT clock_timestamp();";

        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is DateTimeOffset timestamp
            ? timestamp
            : throw new InvalidOperationException(
                "PostgreSQL did not return clock_timestamp() as timestamptz.");
    }

    private static async Task<IngestionAttemptDescriptor?> GetAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IngestionAttemptId attemptId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {AttemptColumns}
            FROM ingest.attempts
            WHERE id = @attempt_id
            {(forUpdate ? "FOR UPDATE" : string.Empty)};
            """;
        AddUuid(command, "attempt_id", attemptId.Value);

        return await ReadAttemptAsync(command, cancellationToken);
    }

    private static async Task<EndpointState?> GetEndpointStateForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EndpointId endpointId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT fence_token, active_attempt_id
            FROM ingest.endpoint_state
            WHERE endpoint_id = @endpoint_id
            FOR UPDATE;
            """;
        AddUuid(command, "endpoint_id", endpointId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new EndpointState(
            new FenceToken(reader.GetInt64(0)),
            reader.IsDBNull(1)
                ? null
                : new IngestionAttemptId(reader.GetGuid(1)));
    }

    private static async Task<CollectionJobDescriptor?> ReadJobAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CollectionJobDescriptor(
            new CollectionJobId(reader.GetGuid(0)),
            new EndpointId(reader.GetGuid(1)),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetInt16(5),
            ParseJobState(reader.GetString(6)),
            reader.IsDBNull(7)
                ? null
                : new WorkerInstanceId(reader.GetGuid(7)),
            new LeaseGeneration(reader.GetInt64(8)),
            reader.IsDBNull(9)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetInt32(10),
            reader.GetFieldValue<DateTimeOffset>(11),
            reader.GetFieldValue<DateTimeOffset>(12),
            reader.IsDBNull(13)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(13));
    }

    private static async Task<IngestionAttemptDescriptor?> ReadAttemptAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new IngestionAttemptDescriptor(
            new IngestionAttemptId(reader.GetGuid(0)),
            new CollectionJobId(reader.GetGuid(1)),
            reader.GetInt32(2),
            new LeaseGeneration(reader.GetInt64(3)),
            reader.IsDBNull(4)
                ? null
                : new FenceToken(reader.GetInt64(4)),
            ParseAttemptState(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.GetFieldValue<DateTimeOffset>(15),
            reader.GetFieldValue<DateTimeOffset>(16));
    }

    private static CollectionJobState ParseJobState(string value) =>
        value switch
        {
            "pending" => CollectionJobState.Pending,
            "leased" => CollectionJobState.Leased,
            "processing" => CollectionJobState.Processing,
            "completed" => CollectionJobState.Completed,
            "failed" => CollectionJobState.Failed,
            "cancelled" => CollectionJobState.Cancelled,
            _ => throw new InvalidOperationException($"Unknown collection job state '{value}'."),
        };

    private static IngestionAttemptState ParseAttemptState(string value) =>
        value switch
        {
            "created" => IngestionAttemptState.Created,
            "fenced" => IngestionAttemptState.Fenced,
            "exchange_authorized" => IngestionAttemptState.ExchangeAuthorized,
            "raw_durable" => IngestionAttemptState.RawDurable,
            "completed" => IngestionAttemptState.Completed,
            "failed" => IngestionAttemptState.Failed,
            "uncertain" => IngestionAttemptState.Uncertain,
            "superseded" => IngestionAttemptState.Superseded,
            "captured_late" => IngestionAttemptState.CapturedLate,
            _ => throw new InvalidOperationException($"Unknown ingestion attempt state '{value}'."),
        };

    private static void AddUuid(NpgsqlCommand command, string name, Guid value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = value;

    private static void AddText(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value;

    private static void AddBigint(NpgsqlCommand command, string name, long value) =>
        command.Parameters.Add(name, NpgsqlDbType.Bigint).Value = value;

    private static void AddTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value = value;

    private static void AddInterval(
        NpgsqlCommand command,
        string name,
        TimeSpan value) =>
        command.Parameters.Add(name, NpgsqlDbType.Interval).Value = value;

    private static string PrefixColumns(string columns, string alias) =>
        string.Join(
            ", ",
            columns
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(column => $"{alias}.{column}"));

    private sealed record EndpointState(
        FenceToken FenceToken,
        IngestionAttemptId? ActiveAttemptId);
}
