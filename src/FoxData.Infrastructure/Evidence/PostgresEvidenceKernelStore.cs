using System.Data;
using FoxData.Application.Evidence;
using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Evidence;

public sealed class PostgresEvidenceKernelStore(NpgsqlDataSource dataSource) : IEvidenceKernelStore
{
    private const string FetchColumns =
        """
        id, attempt_id, endpoint_id, request_started_at, response_started_at, retrieved_at,
        transport_kind, status_code, media_type, content_encoding, declared_length, source_etag,
        cache_control, expires_at, payload_id, prior_fetch_id, duration_ms, created_at
        """;

    private const string PayloadColumns =
        """
        id, sha256, byte_length, body, created_at
        """;

    public async Task<CaptureResult> CaptureSourceResponseAsync(
        FetchId proposedFetchId,
        PayloadId? proposedPayloadId,
        IngestionAttemptId attemptId,
        EndpointId endpointId,
        LeaseGeneration expectedLeaseGeneration,
        FenceToken expectedFenceToken,
        SourceResponseObservation observation,
        PayloadHash? payloadHash,
        ReadOnlyMemory<byte>? body,
        FetchId? priorFetchId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var existing = await GetFetchByAttemptAsync(connection, transaction: null, attemptId, cancellationToken);
        if (existing is not null)
        {
            return await BuildAlreadyCapturedResultAsync(
                connection,
                existing,
                endpointId,
                payloadHash,
                body,
                cancellationToken);
        }

        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var attempt = await GetAttemptContextForUpdateAsync(
            connection,
            transaction,
            attemptId,
            cancellationToken);

        if (attempt is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CaptureResult(CaptureStatus.InvalidAttempt, null, null);
        }

        existing = await GetFetchByAttemptAsync(
            connection,
            transaction,
            attemptId,
            cancellationToken);

        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return await BuildAlreadyCapturedResultAsync(
                connection,
                existing,
                endpointId,
                payloadHash,
                body,
                cancellationToken);
        }

        if (attempt.EndpointId != endpointId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CaptureResult(CaptureStatus.EndpointMismatch, null, null);
        }

        if (attempt.LeaseGeneration != expectedLeaseGeneration)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CaptureResult(CaptureStatus.LeaseGenerationMismatch, null, null);
        }

        if (attempt.FenceToken is null ||
            attempt.FenceToken.Value != expectedFenceToken)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CaptureResult(CaptureStatus.FenceMismatch, null, null);
        }

        if (attempt.ExchangeAuthorizedAt is null ||
            attempt.State is not ("exchange_authorized" or "uncertain" or "superseded"))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CaptureResult(CaptureStatus.InvalidState, null, null);
        }

        if (priorFetchId is not null)
        {
            var prior = await GetFetchAsync(
                connection,
                transaction,
                priorFetchId.Value,
                cancellationToken);

            if (prior is null ||
                prior.EndpointId != endpointId ||
                prior.PayloadId is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CaptureResult(CaptureStatus.InvalidPriorFetch, null, null);
            }
        }

        PayloadDescriptor? payload = null;
        if (body is not null)
        {
            if (payloadHash is null || proposedPayloadId is null)
            {
                throw new EvidenceIntegrityException(
                    "Body capture requires both a payload hash and proposed PayloadId.");
            }

            payload = await InsertOrReusePayloadAsync(
                connection,
                transaction,
                proposedPayloadId.Value,
                payloadHash.Value,
                body.Value,
                cancellationToken);
        }
        else if (payloadHash is not null || proposedPayloadId is not null)
        {
            throw new EvidenceIntegrityException(
                "No-body capture must not carry payload identity.");
        }

        var fetch = await InsertFetchAsync(
            connection,
            transaction,
            proposedFetchId,
            attemptId,
            endpointId,
            observation,
            payload?.Id,
            priorFetchId,
            cancellationToken);

        var endpointState = await GetEndpointStateForUpdateAsync(
            connection,
            transaction,
            endpointId,
            cancellationToken)
            ?? throw new EvidenceIntegrityException(
                $"Endpoint state is missing for endpoint {endpointId}.");

        var leaseCurrent =
            attempt.JobState is "processing" &&
            attempt.JobLeaseGeneration == expectedLeaseGeneration &&
            attempt.JobLeaseExpiresAt is not null &&
            attempt.JobLeaseExpiresAt > endpointState.DatabaseNow;

        var fenceCurrent =
            endpointState.FenceToken == expectedFenceToken &&
            endpointState.ActiveAttemptId == attemptId;

        var authoritative = leaseCurrent && fenceCurrent;

        if (authoritative)
        {
            await CompleteCurrentCaptureAsync(
                connection,
                transaction,
                attemptId,
                attempt.JobId,
                endpointId,
                expectedLeaseGeneration,
                cancellationToken);
        }
        else
        {
            await CompleteLateCaptureAsync(
                connection,
                transaction,
                attemptId,
                attempt.JobId,
                expectedLeaseGeneration,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new CaptureResult(
            authoritative ? CaptureStatus.CapturedCurrent : CaptureStatus.CapturedLate,
            fetch,
            payload);
    }

    public async Task<FetchDescriptor?> GetFetchByAttemptAsync(
        IngestionAttemptId attemptId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await GetFetchByAttemptAsync(
            connection,
            transaction: null,
            attemptId,
            cancellationToken);
    }

    public async Task<FetchDescriptor?> GetFetchAsync(
        FetchId fetchId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await GetFetchAsync(
            connection,
            transaction: null,
            fetchId,
            cancellationToken);
    }

    public async Task<PayloadDescriptor?> GetPayloadAsync(
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await GetPayloadAsync(
            connection,
            transaction: null,
            payloadId,
            cancellationToken);
    }

    private static async Task<CaptureResult> BuildAlreadyCapturedResultAsync(
        NpgsqlConnection connection,
        FetchDescriptor existing,
        EndpointId endpointId,
        PayloadHash? suppliedHash,
        ReadOnlyMemory<byte>? suppliedBody,
        CancellationToken cancellationToken)
    {
        if (existing.EndpointId != endpointId)
        {
            throw new EvidenceIntegrityException(
                "Existing attempt evidence belongs to a different endpoint.");
        }

        PayloadDescriptor? payload = null;
        if (existing.PayloadId is { } payloadId)
        {
            payload = await GetPayloadAsync(
                connection,
                transaction: null,
                payloadId,
                cancellationToken)
                ?? throw new EvidenceIntegrityException(
                    $"Fetch {existing.Id} references missing payload {payloadId}.");

            if (suppliedHash is null ||
                suppliedBody is null ||
                suppliedHash.Value != payload.Hash ||
                suppliedBody.Value.Length != payload.ByteLength ||
                !suppliedBody.Value.Span.SequenceEqual(payload.Body.Span))
            {
                throw new EvidenceIntegrityException(
                    "Repeated capture input differs from the durable payload for this attempt.");
            }
        }
        else if (suppliedHash is not null || suppliedBody is not null)
        {
            throw new EvidenceIntegrityException(
                "Repeated capture supplied a body for an attempt whose durable fetch has no body.");
        }

        return new CaptureResult(CaptureStatus.AlreadyCaptured, existing, payload);
    }

    private static async Task<PayloadDescriptor> InsertOrReusePayloadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PayloadId proposedPayloadId,
        PayloadHash hash,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                $"""
                INSERT INTO evidence.payloads (id, sha256, byte_length, body)
                VALUES (@id, @sha256, @byte_length, @body)
                ON CONFLICT (sha256) DO NOTHING
                RETURNING {PayloadColumns};
                """;

            AddUuid(insert, "id", proposedPayloadId.Value);
            AddBytea(insert, "sha256", hash.ToByteArray());
            AddBigint(insert, "byte_length", body.Length);
            AddBytea(insert, "body", body.ToArray());

            var inserted = await ReadPayloadAsync(insert, cancellationToken);
            if (inserted is not null)
            {
                return inserted;
            }
        }

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            $"""
            SELECT {PayloadColumns}
            FROM evidence.payloads
            WHERE sha256 = @sha256;
            """;
        AddBytea(select, "sha256", hash.ToByteArray());

        var existing = await ReadPayloadAsync(select, cancellationToken)
            ?? throw new EvidenceIntegrityException(
                "Payload hash conflict was observed but the existing payload was not readable.");

        if (existing.ByteLength != body.Length ||
            !existing.Body.Span.SequenceEqual(body.Span))
        {
            throw new EvidenceIntegrityException(
                $"SHA-256 collision or storage corruption detected for payload hash {hash}.");
        }

        return existing;
    }

    private static async Task<FetchDescriptor> InsertFetchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FetchId fetchId,
        IngestionAttemptId attemptId,
        EndpointId endpointId,
        SourceResponseObservation observation,
        PayloadId? payloadId,
        FetchId? priorFetchId,
        CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            $"""
            INSERT INTO evidence.fetches
                (id, attempt_id, endpoint_id, request_started_at, response_started_at, retrieved_at,
                 transport_kind, status_code, media_type, content_encoding, declared_length, source_etag,
                 cache_control, expires_at, payload_id, prior_fetch_id, duration_ms)
            VALUES
                (@id, @attempt_id, @endpoint_id, @request_started_at, @response_started_at, @retrieved_at,
                 @transport_kind, @status_code, @media_type, @content_encoding, @declared_length, @source_etag,
                 @cache_control, @expires_at, @payload_id, @prior_fetch_id, @duration_ms)
            RETURNING {FetchColumns};
            """;

        AddUuid(insert, "id", fetchId.Value);
        AddUuid(insert, "attempt_id", attemptId.Value);
        AddUuid(insert, "endpoint_id", endpointId.Value);
        AddTimestamp(insert, "request_started_at", observation.RequestStartedAt);
        AddNullableTimestamp(insert, "response_started_at", observation.ResponseStartedAt);
        AddTimestamp(insert, "retrieved_at", observation.RetrievedAt);
        AddText(insert, "transport_kind", observation.TransportKind);
        AddNullableInteger(insert, "status_code", observation.StatusCode);
        AddNullableText(insert, "media_type", observation.MediaType);
        AddNullableText(insert, "content_encoding", observation.ContentEncoding);
        AddNullableBigint(insert, "declared_length", observation.DeclaredLength);
        AddNullableText(insert, "source_etag", observation.SourceEtag);
        AddNullableText(insert, "cache_control", observation.CacheControl);
        AddNullableTimestamp(insert, "expires_at", observation.ExpiresAt);
        AddNullableUuid(insert, "payload_id", payloadId?.Value);
        AddNullableUuid(insert, "prior_fetch_id", priorFetchId?.Value);
        AddBigint(insert, "duration_ms", observation.DurationMs);

        return await ReadFetchAsync(insert, cancellationToken)
            ?? throw new EvidenceIntegrityException("Fetch insert returned no row.");
    }

    private static async Task CompleteCurrentCaptureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionAttemptId attemptId,
        CollectionJobId jobId,
        EndpointId endpointId,
        LeaseGeneration leaseGeneration,
        CancellationToken cancellationToken)
    {
        await using (var attempt = connection.CreateCommand())
        {
            attempt.Transaction = transaction;
            attempt.CommandText =
                """
                UPDATE ingest.attempts
                SET state = 'completed',
                    outcome_code = 'captured_current',
                    raw_durable_at = transaction_timestamp(),
                    completed_at = transaction_timestamp(),
                    updated_at = transaction_timestamp()
                WHERE id = @attempt_id
                  AND exchange_authorized_at IS NOT NULL;
                """;
            AddUuid(attempt, "attempt_id", attemptId.Value);

            if (await attempt.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new EvidenceIntegrityException(
                    "Current capture could not finalize its attempt.");
            }
        }

        await using (var job = connection.CreateCommand())
        {
            job.Transaction = transaction;
            job.CommandText =
                """
                UPDATE ingest.collection_jobs
                SET state = 'completed',
                    lease_owner_id = NULL,
                    lease_expires_at = NULL,
                    completed_at = transaction_timestamp(),
                    updated_at = transaction_timestamp()
                WHERE id = @job_id
                  AND lease_generation = @lease_generation
                  AND state = 'processing';
                """;
            AddUuid(job, "job_id", jobId.Value);
            AddBigint(job, "lease_generation", leaseGeneration.Value);

            if (await job.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new EvidenceIntegrityException(
                    "Current capture could not finalize its collection job.");
            }
        }

        await using var endpoint = connection.CreateCommand();
        endpoint.Transaction = transaction;
        endpoint.CommandText =
            """
            UPDATE ingest.endpoint_state
            SET active_attempt_id = NULL,
                last_authoritative_attempt_id = @attempt_id,
                updated_at = transaction_timestamp()
            WHERE endpoint_id = @endpoint_id
              AND active_attempt_id = @attempt_id;
            """;
        AddUuid(endpoint, "attempt_id", attemptId.Value);
        AddUuid(endpoint, "endpoint_id", endpointId.Value);

        if (await endpoint.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new EvidenceIntegrityException(
                "Current capture could not advance endpoint authority.");
        }
    }

    private static async Task CompleteLateCaptureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionAttemptId attemptId,
        CollectionJobId jobId,
        LeaseGeneration leaseGeneration,
        CancellationToken cancellationToken)
    {
        await using (var attempt = connection.CreateCommand())
        {
            attempt.Transaction = transaction;
            attempt.CommandText =
                """
                UPDATE ingest.attempts
                SET state = 'captured_late',
                    outcome_code = 'captured_late',
                    raw_durable_at = transaction_timestamp(),
                    completed_at = transaction_timestamp(),
                    superseded_at = COALESCE(superseded_at, transaction_timestamp()),
                    updated_at = transaction_timestamp()
                WHERE id = @attempt_id
                  AND exchange_authorized_at IS NOT NULL;
                """;
            AddUuid(attempt, "attempt_id", attemptId.Value);

            if (await attempt.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new EvidenceIntegrityException(
                    "Late capture could not finalize its attempt.");
            }
        }

        await using var job = connection.CreateCommand();
        job.Transaction = transaction;
        job.CommandText =
            """
            UPDATE ingest.collection_jobs
            SET state = 'completed',
                lease_owner_id = NULL,
                lease_expires_at = NULL,
                completed_at = transaction_timestamp(),
                updated_at = transaction_timestamp()
            WHERE id = @job_id
              AND lease_generation = @lease_generation
              AND state = 'processing'
              AND lease_expires_at > transaction_timestamp();
            """;
        AddUuid(job, "job_id", jobId.Value);
        AddBigint(job, "lease_generation", leaseGeneration.Value);

        await job.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AttemptContext?> GetAttemptContextForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionAttemptId attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                attempt.job_id,
                attempt.lease_generation,
                attempt.fence_token,
                attempt.state,
                attempt.exchange_authorized_at,
                job.endpoint_id,
                job.state,
                job.lease_generation,
                job.lease_expires_at
            FROM ingest.attempts AS attempt
            JOIN ingest.collection_jobs AS job ON job.id = attempt.job_id
            WHERE attempt.id = @attempt_id
            FOR UPDATE OF attempt, job;
            """;
        AddUuid(command, "attempt_id", attemptId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AttemptContext(
            new CollectionJobId(reader.GetGuid(0)),
            new LeaseGeneration(reader.GetInt64(1)),
            reader.IsDBNull(2) ? null : new FenceToken(reader.GetInt64(2)),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            new EndpointId(reader.GetGuid(5)),
            reader.GetString(6),
            new LeaseGeneration(reader.GetInt64(7)),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));
    }

    private static async Task<EndpointStateContext?> GetEndpointStateForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EndpointId endpointId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                fence_token,
                active_attempt_id,
                transaction_timestamp()
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

        return new EndpointStateContext(
            new FenceToken(reader.GetInt64(0)),
            reader.IsDBNull(1) ? null : new IngestionAttemptId(reader.GetGuid(1)),
            reader.GetFieldValue<DateTimeOffset>(2));
    }

    private static async Task<FetchDescriptor?> GetFetchByAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IngestionAttemptId attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {FetchColumns}
            FROM evidence.fetches
            WHERE attempt_id = @attempt_id;
            """;
        AddUuid(command, "attempt_id", attemptId.Value);

        return await ReadFetchAsync(command, cancellationToken);
    }

    private static async Task<FetchDescriptor?> GetFetchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        FetchId fetchId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {FetchColumns}
            FROM evidence.fetches
            WHERE id = @fetch_id;
            """;
        AddUuid(command, "fetch_id", fetchId.Value);

        return await ReadFetchAsync(command, cancellationToken);
    }

    private static async Task<PayloadDescriptor?> GetPayloadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {PayloadColumns}
            FROM evidence.payloads
            WHERE id = @payload_id;
            """;
        AddUuid(command, "payload_id", payloadId.Value);

        return await ReadPayloadAsync(command, cancellationToken);
    }

    private static async Task<FetchDescriptor?> ReadFetchAsync(
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

        return new FetchDescriptor(
            new FetchId(reader.GetGuid(0)),
            new IngestionAttemptId(reader.GetGuid(1)),
            new EndpointId(reader.GetGuid(2)),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
            reader.IsDBNull(14) ? null : new PayloadId(reader.GetGuid(14)),
            reader.IsDBNull(15) ? null : new FetchId(reader.GetGuid(15)),
            reader.GetInt64(16),
            reader.GetFieldValue<DateTimeOffset>(17));
    }

    private static async Task<PayloadDescriptor?> ReadPayloadAsync(
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

        var body = reader.GetFieldValue<byte[]>(3);

        return new PayloadDescriptor(
            new PayloadId(reader.GetGuid(0)),
            PayloadHash.Parse(Convert.ToHexString(reader.GetFieldValue<byte[]>(1)).ToLowerInvariant()),
            reader.GetInt64(2),
            body,
            reader.GetFieldValue<DateTimeOffset>(4));
    }

    private static void AddUuid(NpgsqlCommand command, string name, Guid value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = value;

    private static void AddNullableUuid(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = value is null ? DBNull.Value : value.Value;

    private static void AddText(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value;

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value is null ? DBNull.Value : value;

    private static void AddBigint(NpgsqlCommand command, string name, long value) =>
        command.Parameters.Add(name, NpgsqlDbType.Bigint).Value = value;

    private static void AddNullableBigint(NpgsqlCommand command, string name, long? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Bigint).Value = value is null ? DBNull.Value : value.Value;

    private static void AddNullableInteger(NpgsqlCommand command, string name, int? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Integer).Value = value is null ? DBNull.Value : value.Value;

    private static void AddBytea(NpgsqlCommand command, string name, byte[] value) =>
        command.Parameters.Add(name, NpgsqlDbType.Bytea).Value = value;

    private static void AddTimestamp(NpgsqlCommand command, string name, DateTimeOffset value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value = value;

    private static void AddNullableTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset? value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value =
            value is null ? DBNull.Value : value.Value;

    private sealed record AttemptContext(
        CollectionJobId JobId,
        LeaseGeneration LeaseGeneration,
        FenceToken? FenceToken,
        string State,
        DateTimeOffset? ExchangeAuthorizedAt,
        EndpointId EndpointId,
        string JobState,
        LeaseGeneration JobLeaseGeneration,
        DateTimeOffset? JobLeaseExpiresAt);

    private sealed record EndpointStateContext(
        FenceToken FenceToken,
        IngestionAttemptId? ActiveAttemptId,
        DateTimeOffset DatabaseNow);
}
