using System.Data;
using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Runtime;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Canonical;

public sealed class PostgresCoverageStore(NpgsqlDataSource dataSource)
    : ICoverageStore
{
    public async Task<IReadOnlyList<CoverageAttemptEvidence>> GetUncoveredAttemptsAsync(
        string sourceKey,
        IReadOnlyList<CoverageCapabilityPlan> capabilities,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ValidateRequiredText(sourceKey, 128, nameof(sourceKey));
        ValidateCapabilityPlans(capabilities);
        ValidateBatchSize(batchSize);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH capability_plan AS (
                SELECT *
                FROM unnest(
                    @capability_keys::text[],
                    @parser_versions::text[],
                    @normalizer_versions::text[],
                    @dependency_ranks::integer[])
                    AS plan(
                        capability_key,
                        parser_version,
                        normalizer_version,
                        dependency_rank)
            )
            SELECT
                attempt.id,
                job.id,
                endpoint.id,
                shard.id,
                source.key,
                shard.environment,
                shard.key,
                endpoint.capability_key,
                endpoint.semantic_key,
                attempt.state,
                attempt.outcome_code,
                attempt.error_code,
                attempt.started_at,
                attempt.completed_at,
                validation_fetch.id,
                representation_fetch.id,
                validation_fetch.retrieved_at,
                validation_fetch.status_code,
                validation_fetch.body_error_code,
                representation_fetch.content_encoding,
                payload.body,
                parse_run.id,
                parse_run.outcome,
                parse_run.error_code
            FROM ingest.attempts AS attempt
            INNER JOIN ingest.collection_jobs AS job
                ON job.id = attempt.job_id
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = job.endpoint_id
            INNER JOIN capability_plan AS plan
                ON plan.capability_key = endpoint.capability_key
            INNER JOIN sources.shards AS shard
                ON shard.id = endpoint.shard_id
            INNER JOIN sources.sources AS source
                ON source.id = shard.source_id
            LEFT JOIN evidence.fetches AS validation_fetch
                ON validation_fetch.attempt_id = attempt.id
            LEFT JOIN evidence.fetches AS representation_fetch
                ON representation_fetch.id =
                    CASE
                        WHEN validation_fetch.payload_id IS NOT NULL
                            THEN validation_fetch.id
                        WHEN validation_fetch.status_code = 304
                            THEN validation_fetch.prior_fetch_id
                        ELSE NULL
                    END
            LEFT JOIN evidence.payloads AS payload
                ON payload.id = representation_fetch.payload_id
            LEFT JOIN evidence.source_parse_runs AS parse_run
                ON parse_run.representation_fetch_id = representation_fetch.id
               AND parse_run.capability_key = endpoint.capability_key
               AND parse_run.parser_version = plan.parser_version
            LEFT JOIN evidence.coverage_observations AS coverage
                ON coverage.attempt_id = attempt.id
            WHERE source.key = @source_key
              AND attempt.state IN
                  ('completed', 'failed', 'uncertain', 'superseded', 'captured_late')
              AND coverage.id IS NULL
            ORDER BY
                COALESCE(
                    validation_fetch.retrieved_at,
                    attempt.completed_at,
                    attempt.started_at),
                attempt.id
            LIMIT @batch_size;
            """;

        AddText(command, "source_key", sourceKey);
        AddCapabilityPlan(command, capabilities);
        command.Parameters.Add("batch_size", NpgsqlDbType.Integer).Value =
            batchSize;

        var results = new List<CoverageAttemptEvidence>(batchSize);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new CoverageAttemptEvidence(
                    new IngestionAttemptId(reader.GetGuid(0)),
                    new CollectionJobId(reader.GetGuid(1)),
                    new EndpointId(reader.GetGuid(2)),
                    new ShardId(reader.GetGuid(3)),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.GetFieldValue<DateTimeOffset>(12),
                    reader.IsDBNull(13)
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(13),
                    reader.IsDBNull(14)
                        ? null
                        : new FetchId(reader.GetGuid(14)),
                    reader.IsDBNull(15)
                        ? null
                        : new FetchId(reader.GetGuid(15)),
                    reader.IsDBNull(16)
                        ? null
                        : reader.GetFieldValue<DateTimeOffset>(16),
                    reader.IsDBNull(17) ? null : reader.GetInt32(17),
                    reader.IsDBNull(18) ? null : reader.GetString(18),
                    reader.IsDBNull(19) ? null : reader.GetString(19),
                    reader.IsDBNull(20)
                        ? null
                        : reader.GetFieldValue<byte[]>(20),
                    reader.IsDBNull(21)
                        ? null
                        : new SourceParseRunId(reader.GetGuid(21)),
                    reader.IsDBNull(22) ? null : reader.GetString(22),
                    reader.IsDBNull(23) ? null : reader.GetString(23)));
        }

        return results;
    }

    public async Task<CoverageObservationDescriptor> RecordAsync(
        CoverageObservationWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        ValidateCoverageWrite(write);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        await EnsureCoverageProvenanceAsync(
            connection,
            transaction,
            write,
            cancellationToken);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO evidence.coverage_observations
                (id, endpoint_id, collection_job_id, attempt_id,
                 validation_fetch_id, representation_fetch_id,
                 source_parse_run_id, state, boundary_at, detail_code)
            VALUES
                (@id, @endpoint_id, @collection_job_id, @attempt_id,
                 @validation_fetch_id, @representation_fetch_id,
                 @source_parse_run_id, @state, @boundary_at, @detail_code)
            ON CONFLICT (attempt_id)
            DO NOTHING
            RETURNING
                id, endpoint_id, collection_job_id, attempt_id,
                validation_fetch_id, representation_fetch_id,
                source_parse_run_id, state, boundary_at, detail_code,
                recorded_at;
            """;

        AddUuid(insert, "id", CoverageObservationId.New().Value);
        AddUuid(insert, "endpoint_id", write.EndpointId.Value);
        AddUuid(insert, "collection_job_id", write.CollectionJobId.Value);
        AddUuid(insert, "attempt_id", write.AttemptId.Value);
        AddNullableUuid(
            insert,
            "validation_fetch_id",
            write.ValidationFetchId?.Value);
        AddNullableUuid(
            insert,
            "representation_fetch_id",
            write.RepresentationFetchId?.Value);
        AddNullableUuid(
            insert,
            "source_parse_run_id",
            write.SourceParseRunId?.Value);
        AddText(insert, "state", ToStorage(write.State));
        AddTimestamp(insert, "boundary_at", write.BoundaryAt);
        AddNullableText(insert, "detail_code", write.DetailCode);

        var created = await ReadCoverageAsync(
            insert,
            cancellationToken);

        if (created is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return created;
        }

        var existing = await GetCoverageByAttemptAsync(
            connection,
            transaction,
            write.AttemptId,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "Coverage uniqueness conflict was observed but the existing row was not readable.");

        EnsureEquivalent(existing, write);

        await transaction.CommitAsync(cancellationToken);
        return existing;
    }

    public async Task<IReadOnlyList<CoverageContinuityCandidate>>
        GetPendingMapContinuityAsync(
            string sourceKey,
            string processorVersion,
            int batchSize,
            CancellationToken cancellationToken)
    {
        ValidateRequiredText(sourceKey, 128, nameof(sourceKey));
        ValidateRequiredText(
            processorVersion,
            128,
            nameof(processorVersion));
        ValidateBatchSize(batchSize);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                coverage.id,
                coverage.endpoint_id,
                coverage.collection_job_id,
                coverage.attempt_id,
                coverage.validation_fetch_id,
                coverage.representation_fetch_id,
                coverage.source_parse_run_id,
                coverage.state,
                coverage.boundary_at,
                coverage.detail_code,
                coverage.recorded_at,
                shard.id,
                source.key,
                shard.key,
                endpoint.capability_key,
                endpoint.semantic_key,
                payload.body,
                representation_fetch.content_encoding,
                parse_run.adapter_version,
                parse_run.parser_version,
                parse_run.fingerprint_algorithm,
                parse_run.structural_fingerprint,
                parse_run.outcome,
                parse_run.unknown_property_count,
                parse_run.unknown_code_count,
                parse_run.decoded_byte_length
            FROM evidence.coverage_observations AS coverage
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = coverage.endpoint_id
            INNER JOIN sources.shards AS shard
                ON shard.id = endpoint.shard_id
            INNER JOIN sources.sources AS source
                ON source.id = shard.source_id
            INNER JOIN evidence.fetches AS validation_fetch
                ON validation_fetch.id = coverage.validation_fetch_id
            INNER JOIN evidence.fetches AS representation_fetch
                ON representation_fetch.id = coverage.representation_fetch_id
            INNER JOIN evidence.payloads AS payload
                ON payload.id = representation_fetch.payload_id
            INNER JOIN evidence.source_parse_runs AS parse_run
                ON parse_run.id = coverage.source_parse_run_id
            LEFT JOIN evidence.coverage_reprocessing_runs AS processed
                ON processed.coverage_observation_id = coverage.id
               AND processed.processor_version = @processor_version
            WHERE source.key = @source_key
              AND endpoint.capability_key = 'active-map-list'
              AND endpoint.semantic_key = 'maps'
              AND coverage.state = 'source_not_modified'
              AND validation_fetch.status_code = 304
              AND validation_fetch.prior_fetch_id = representation_fetch.id
              AND parse_run.outcome IN ('parsed', 'parsed_with_unknowns')
              AND processed.id IS NULL
            ORDER BY coverage.boundary_at, coverage.id
            LIMIT @batch_size;
            """;

        AddText(command, "source_key", sourceKey);
        AddText(command, "processor_version", processorVersion);
        command.Parameters.Add("batch_size", NpgsqlDbType.Integer).Value =
            batchSize;

        var results = new List<CoverageContinuityCandidate>(batchSize);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var coverage = ReadCoverageColumns(reader, 0);
            results.Add(
                new CoverageContinuityCandidate(
                    coverage,
                    new ShardId(reader.GetGuid(11)),
                    reader.GetString(12),
                    reader.GetString(13),
                    reader.GetString(14),
                    reader.GetString(15),
                    reader.GetFieldValue<byte[]>(16),
                    reader.IsDBNull(17) ? null : reader.GetString(17),
                    reader.GetString(18),
                    reader.GetString(19),
                    reader.GetString(20),
                    reader.IsDBNull(21) ? null : reader.GetString(21),
                    reader.GetString(22),
                    reader.GetInt32(23),
                    reader.GetInt32(24),
                    reader.IsDBNull(25) ? null : reader.GetInt64(25)));
        }

        return results;
    }

    public async Task<CoverageContinuityResult> RecordMapContinuityAsync(
        CoverageContinuityWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        ValidateContinuityWrite(write);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        var coverage = await GetCoverageForUpdateAsync(
            connection,
            transaction,
            write.CoverageObservationId,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"Coverage observation {write.CoverageObservationId} does not exist.");

        if (coverage.State != CoverageState.SourceNotModified ||
            coverage.ValidationFetchId is null ||
            coverage.RepresentationFetchId is null ||
            coverage.SourceParseRunId is null ||
            coverage.BoundaryAt != write.ObservedAt)
        {
            throw new CanonicalStateIntegrityException(
                "Map continuity write does not match a source-not-modified coverage observation.");
        }

        await EnsureContinuityProvenanceAsync(
            connection,
            transaction,
            coverage,
            write,
            cancellationToken);

        var warContext = await EnsureWarContextAsync(
            connection,
            transaction,
            write.ShardId,
            write.WarId,
            write.ObservedAt,
            cancellationToken);

        var memberships =
            await UpsertMembershipsAsync(
                connection,
                transaction,
                write,
                cancellationToken);

        var run = await RecordCoverageRunAsync(
            connection,
            transaction,
            write.CoverageObservationId,
            write.ProcessorVersion,
            "applied",
            errorCode: null,
            write.StartedAt,
            write.CompletedAt,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new CoverageContinuityResult(
            run,
            warContext,
            memberships);
    }

    public async Task<CoverageReprocessingRunDescriptor>
        RecordMapContinuityRejectedAsync(
            CoverageObservationId coverageObservationId,
            string processorVersion,
            DateTimeOffset startedAt,
            DateTimeOffset completedAt,
            string errorCode,
            CancellationToken cancellationToken)
    {
        ValidateId(
            coverageObservationId.Value,
            nameof(coverageObservationId));
        ValidateRequiredText(
            processorVersion,
            128,
            nameof(processorVersion));
        ValidateRequiredText(errorCode, 128, nameof(errorCode));

        if (completedAt < startedAt)
        {
            throw new ArgumentException(
                "Coverage processing completion must not precede start.",
                nameof(completedAt));
        }

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        var coverage = await GetCoverageForUpdateAsync(
            connection,
            transaction,
            coverageObservationId,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"Coverage observation {coverageObservationId} does not exist.");

        if (coverage.State != CoverageState.SourceNotModified)
        {
            throw new CanonicalStateIntegrityException(
                "Only source_not_modified coverage can terminate map continuity processing.");
        }

        var run = await RecordCoverageRunAsync(
            connection,
            transaction,
            coverageObservationId,
            processorVersion,
            "rejected",
            errorCode,
            startedAt,
            completedAt,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return run;
    }

    public async Task<bool> IsMapContinuityAppliedAsync(
        FetchId validationFetchId,
        string processorVersion,
        CancellationToken cancellationToken)
    {
        if (validationFetchId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Validation Fetch identifier must not be empty.",
                nameof(validationFetchId));
        }

        ValidateRequiredText(
            processorVersion,
            128,
            nameof(processorVersion));

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1
                FROM evidence.coverage_observations AS coverage
                INNER JOIN evidence.coverage_reprocessing_runs AS run
                    ON run.coverage_observation_id = coverage.id
                WHERE coverage.validation_fetch_id = @validation_fetch_id
                  AND coverage.state = 'source_not_modified'
                  AND run.processor_version = @processor_version
                  AND run.outcome = 'applied'
            );
            """;

        AddUuid(command, "validation_fetch_id", validationFetchId.Value);
        AddText(command, "processor_version", processorVersion);

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task<IReadOnlyList<CanonicalReprocessingCandidate>>
        GetPendingCanonicalReprocessingAsync(
            string sourceKey,
            IReadOnlyList<CoverageCapabilityPlan> capabilities,
            int batchSize,
            CancellationToken cancellationToken)
    {
        ValidateRequiredText(sourceKey, 128, nameof(sourceKey));
        ValidateCapabilityPlans(capabilities);
        ValidateBatchSize(batchSize);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH capability_plan AS (
                SELECT *
                FROM unnest(
                    @capability_keys::text[],
                    @parser_versions::text[],
                    @normalizer_versions::text[],
                    @dependency_ranks::integer[])
                    AS plan(
                        capability_key,
                        parser_version,
                        normalizer_version,
                        dependency_rank)
            )
            SELECT
                parse_run.id,
                endpoint.capability_key,
                representation_fetch.retrieved_at
            FROM evidence.source_parse_runs AS parse_run
            INNER JOIN evidence.fetches AS representation_fetch
                ON representation_fetch.id = parse_run.representation_fetch_id
            INNER JOIN ingest.attempts AS attempt
                ON attempt.id = representation_fetch.attempt_id
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = representation_fetch.endpoint_id
            INNER JOIN capability_plan AS plan
                ON plan.capability_key = endpoint.capability_key
               AND plan.parser_version = parse_run.parser_version
            INNER JOIN sources.shards AS shard
                ON shard.id = endpoint.shard_id
            INNER JOIN sources.sources AS source
                ON source.id = shard.source_id
            LEFT JOIN evidence.normalization_runs AS normalization
                ON normalization.source_parse_run_id = parse_run.id
               AND normalization.normalizer_version =
                   plan.normalizer_version
            WHERE source.key = @source_key
              AND attempt.outcome_code = 'captured_current'
              AND normalization.id IS NULL
            ORDER BY
                plan.dependency_rank,
                representation_fetch.retrieved_at,
                parse_run.id
            LIMIT @batch_size;
            """;

        AddText(command, "source_key", sourceKey);
        AddCapabilityPlan(command, capabilities);
        command.Parameters.Add("batch_size", NpgsqlDbType.Integer).Value =
            batchSize;

        var results =
            new List<CanonicalReprocessingCandidate>(batchSize);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new CanonicalReprocessingCandidate(
                    new SourceParseRunId(reader.GetGuid(0)),
                    reader.GetString(1),
                    reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return results;
    }

    private static async Task EnsureCoverageProvenanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoverageObservationWrite write,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                job.id,
                job.endpoint_id,
                validation_fetch.id,
                validation_fetch.endpoint_id,
                validation_fetch.status_code,
                validation_fetch.prior_fetch_id,
                representation_fetch.id,
                representation_fetch.endpoint_id,
                representation_fetch.payload_id,
                parse_run.id,
                parse_run.representation_fetch_id,
                parse_run.capability_key,
                endpoint.capability_key
            FROM ingest.attempts AS attempt
            INNER JOIN ingest.collection_jobs AS job
                ON job.id = attempt.job_id
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = job.endpoint_id
            LEFT JOIN evidence.fetches AS validation_fetch
                ON validation_fetch.attempt_id = attempt.id
            LEFT JOIN evidence.fetches AS representation_fetch
                ON representation_fetch.id = @representation_fetch_id
            LEFT JOIN evidence.source_parse_runs AS parse_run
                ON parse_run.id = @source_parse_run_id
            WHERE attempt.id = @attempt_id;
            """;

        AddUuid(command, "attempt_id", write.AttemptId.Value);
        AddNullableUuid(
            command,
            "representation_fetch_id",
            write.RepresentationFetchId?.Value);
        AddNullableUuid(
            command,
            "source_parse_run_id",
            write.SourceParseRunId?.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CanonicalStateIntegrityException(
                $"Coverage attempt {write.AttemptId} does not exist.");
        }

        var jobId = new CollectionJobId(reader.GetGuid(0));
        var endpointId = new EndpointId(reader.GetGuid(1));
        FetchId? durableValidationFetchId =
            reader.IsDBNull(2)
                ? null
                : new FetchId(reader.GetGuid(2));
        EndpointId? validationEndpointId =
            reader.IsDBNull(3)
                ? null
                : new EndpointId(reader.GetGuid(3));
        int? validationStatus =
            reader.IsDBNull(4) ? null : reader.GetInt32(4);
        FetchId? priorFetchId =
            reader.IsDBNull(5)
                ? null
                : new FetchId(reader.GetGuid(5));
        FetchId? representationFetchId =
            reader.IsDBNull(6)
                ? null
                : new FetchId(reader.GetGuid(6));
        EndpointId? representationEndpointId =
            reader.IsDBNull(7)
                ? null
                : new EndpointId(reader.GetGuid(7));
        PayloadId? representationPayloadId =
            reader.IsDBNull(8)
                ? null
                : new PayloadId(reader.GetGuid(8));
        SourceParseRunId? sourceParseRunId =
            reader.IsDBNull(9)
                ? null
                : new SourceParseRunId(reader.GetGuid(9));
        FetchId? parseRepresentationFetchId =
            reader.IsDBNull(10)
                ? null
                : new FetchId(reader.GetGuid(10));
        var parseCapabilityKey =
            reader.IsDBNull(11) ? null : reader.GetString(11);
        var endpointCapabilityKey = reader.GetString(12);

        if (jobId != write.CollectionJobId ||
            endpointId != write.EndpointId ||
            durableValidationFetchId != write.ValidationFetchId)
        {
            throw new CanonicalStateIntegrityException(
                "Coverage write does not match its durable attempt/job/Fetch provenance.");
        }

        if (write.ValidationFetchId is not null &&
            validationEndpointId != write.EndpointId)
        {
            throw new CanonicalStateIntegrityException(
                "Coverage validation Fetch belongs to a different endpoint.");
        }

        if (write.RepresentationFetchId is not null &&
            (representationFetchId != write.RepresentationFetchId ||
             representationEndpointId != write.EndpointId ||
             representationPayloadId is null))
        {
            throw new CanonicalStateIntegrityException(
                "Coverage representation does not reference a body-bearing Fetch from the same endpoint.");
        }

        if (write.SourceParseRunId is not null &&
            (sourceParseRunId != write.SourceParseRunId ||
             parseRepresentationFetchId != write.RepresentationFetchId ||
             !string.Equals(
                 parseCapabilityKey,
                 endpointCapabilityKey,
                 StringComparison.Ordinal)))
        {
            throw new CanonicalStateIntegrityException(
                "Coverage source parse does not match its representation/capability provenance.");
        }

        if (write.State == CoverageState.SourceNotModified &&
            (validationStatus != 304 ||
             priorFetchId != write.RepresentationFetchId ||
             write.ValidationFetchId == write.RepresentationFetchId ||
             write.SourceParseRunId is null))
        {
            throw new CanonicalStateIntegrityException(
                "source_not_modified coverage requires an exact 304 -> body-bearing representation -> parse lineage.");
        }

        if (write.State == CoverageState.Observed &&
            (write.ValidationFetchId is null ||
             write.ValidationFetchId != write.RepresentationFetchId ||
             write.SourceParseRunId is null))
        {
            throw new CanonicalStateIntegrityException(
                "observed coverage requires one body-bearing validation representation and source parse.");
        }
    }

    private static async Task EnsureContinuityProvenanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoverageObservationDescriptor coverage,
        CoverageContinuityWrite write,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                endpoint.shard_id,
                endpoint.capability_key,
                endpoint.semantic_key,
                validation_fetch.status_code,
                validation_fetch.prior_fetch_id,
                representation_fetch.payload_id,
                parse_run.representation_fetch_id,
                parse_run.outcome
            FROM evidence.coverage_observations AS coverage
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = coverage.endpoint_id
            INNER JOIN evidence.fetches AS validation_fetch
                ON validation_fetch.id = coverage.validation_fetch_id
            INNER JOIN evidence.fetches AS representation_fetch
                ON representation_fetch.id = coverage.representation_fetch_id
            INNER JOIN evidence.source_parse_runs AS parse_run
                ON parse_run.id = coverage.source_parse_run_id
            WHERE coverage.id = @coverage_id;
            """;

        AddUuid(command, "coverage_id", coverage.Id.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CanonicalStateIntegrityException(
                "Coverage continuity provenance disappeared.");
        }

        var shardId = new ShardId(reader.GetGuid(0));
        var capabilityKey = reader.GetString(1);
        var semanticKey = reader.GetString(2);
        var statusCode = reader.GetInt32(3);
        var priorFetchId = new FetchId(reader.GetGuid(4));
        var hasPayload = !reader.IsDBNull(5);
        var parseRepresentationFetchId =
            new FetchId(reader.GetGuid(6));
        var parseOutcome = reader.GetString(7);

        if (shardId != write.ShardId ||
            !string.Equals(
                capabilityKey,
                "active-map-list",
                StringComparison.Ordinal) ||
            !string.Equals(
                semanticKey,
                "maps",
                StringComparison.Ordinal) ||
            statusCode != 304 ||
            priorFetchId != coverage.RepresentationFetchId ||
            !hasPayload ||
            parseRepresentationFetchId !=
                coverage.RepresentationFetchId ||
            parseOutcome is not ("parsed" or "parsed_with_unknowns"))
        {
            throw new CanonicalStateIntegrityException(
                "Coverage continuity write does not match exact active-map-list 304 lineage.");
        }
    }

    private static async Task<WarContextDescriptor> EnsureWarContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ShardId shardId,
        WarId warId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

        AddUuid(command, "shard_id", shardId.Value);
        AddTimestamp(command, "observed_at", observedAt);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CanonicalStateIntegrityException(
                "Coverage continuity has no qualifying canonical war context.");
        }

        var context = new WarContextDescriptor(
            new WarId(reader.GetGuid(0)),
            new ShardId(reader.GetGuid(1)),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3));

        if (context.WarId != warId)
        {
            throw new CanonicalStateIntegrityException(
                "Coverage continuity does not target the latest canonical war at its validation boundary.");
        }

        return context;
    }

    private static async Task<IReadOnlyList<RegionMembershipDescriptor>>
        UpsertMembershipsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CoverageContinuityWrite write,
            CancellationToken cancellationToken)
    {
        var results = new List<RegionMembershipDescriptor>(
            write.Memberships.Count);

        foreach (var candidate in write.Memberships
                     .OrderBy(x => x.SourceMapName, StringComparer.Ordinal))
        {
            var region = await GetOrCreateRegionAsync(
                connection,
                transaction,
                candidate,
                cancellationToken);
            var membership = await GetOrCreateWarRegionAsync(
                connection,
                transaction,
                write,
                region,
                candidate,
                cancellationToken);

            results.Add(
                new RegionMembershipDescriptor(
                    region,
                    membership));
        }

        return results;
    }

    private static async Task<RegionDescriptor> GetOrCreateRegionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RegionMembershipCandidate candidate,
        CancellationToken cancellationToken)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO runtime.regions
                    (id, canonical_key, display_name)
                VALUES
                    (@id, @canonical_key, @display_name)
                ON CONFLICT (canonical_key)
                DO NOTHING
                RETURNING
                    id, canonical_key, display_name, created_at;
                """;

            AddUuid(insert, "id", RegionId.New().Value);
            AddText(insert, "canonical_key", candidate.CanonicalKey);
            AddText(insert, "display_name", candidate.DisplayName);

            var created = await ReadRegionAsync(
                insert,
                cancellationToken);
            if (created is not null)
            {
                return created;
            }
        }

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            """
            SELECT id, canonical_key, display_name, created_at
            FROM runtime.regions
            WHERE canonical_key = @canonical_key
            FOR UPDATE;
            """;
        AddText(select, "canonical_key", candidate.CanonicalKey);

        var existing = await ReadRegionAsync(
            select,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "Coverage region uniqueness conflict was not readable.");

        if (!string.Equals(
                existing.DisplayName,
                candidate.DisplayName,
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                $"Canonical region '{candidate.CanonicalKey}' changed display identity.");
        }

        return existing;
    }

    private static async Task<WarRegionDescriptor> GetOrCreateWarRegionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoverageContinuityWrite write,
        RegionDescriptor region,
        RegionMembershipCandidate candidate,
        CancellationToken cancellationToken)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO runtime.war_regions
                    (id, war_id, region_id, source_map_name, source_region_id,
                     first_seen_at, last_seen_at)
                VALUES
                    (@id, @war_id, @region_id, @source_map_name, @source_region_id,
                     @observed_at, @observed_at)
                ON CONFLICT (war_id, source_map_name)
                DO NOTHING
                RETURNING
                    id, war_id, region_id, source_map_name, source_region_id,
                    first_seen_at, last_seen_at, created_at;
                """;

            AddUuid(insert, "id", WarRegionId.New().Value);
            AddUuid(insert, "war_id", write.WarId.Value);
            AddUuid(insert, "region_id", region.Id.Value);
            AddText(insert, "source_map_name", candidate.SourceMapName);
            AddNullableInteger(
                insert,
                "source_region_id",
                candidate.SourceRegionId);
            AddTimestamp(insert, "observed_at", write.ObservedAt);

            var created = await ReadWarRegionAsync(
                insert,
                cancellationToken);
            if (created is not null)
            {
                return created;
            }
        }

        WarRegionDescriptor existing;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT
                    id, war_id, region_id, source_map_name, source_region_id,
                    first_seen_at, last_seen_at, created_at
                FROM runtime.war_regions
                WHERE war_id = @war_id
                  AND source_map_name = @source_map_name
                FOR UPDATE;
                """;

            AddUuid(select, "war_id", write.WarId.Value);
            AddText(select, "source_map_name", candidate.SourceMapName);

            existing = await ReadWarRegionAsync(
                select,
                cancellationToken)
                ?? throw new CanonicalStateIntegrityException(
                    "Coverage war-region uniqueness conflict was not readable.");
        }

        if (existing.RegionId != region.Id)
        {
            throw new CanonicalStateIntegrityException(
                $"War-region '{candidate.SourceMapName}' changed canonical region identity.");
        }

        if (existing.SourceRegionId is { } existingSourceRegionId &&
            candidate.SourceRegionId is { } suppliedSourceRegionId &&
            existingSourceRegionId != suppliedSourceRegionId)
        {
            throw new CanonicalStateIntegrityException(
                $"War-region '{candidate.SourceMapName}' changed source region identifier.");
        }

        var effectiveSourceRegionId =
            existing.SourceRegionId ?? candidate.SourceRegionId;
        var firstSeenAt =
            existing.FirstSeenAt <= write.ObservedAt
                ? existing.FirstSeenAt
                : write.ObservedAt;
        var lastSeenAt =
            existing.LastSeenAt >= write.ObservedAt
                ? existing.LastSeenAt
                : write.ObservedAt;

        if (effectiveSourceRegionId == existing.SourceRegionId &&
            firstSeenAt == existing.FirstSeenAt &&
            lastSeenAt == existing.LastSeenAt)
        {
            return existing;
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE runtime.war_regions
            SET
                source_region_id = @source_region_id,
                first_seen_at = @first_seen_at,
                last_seen_at = @last_seen_at
            WHERE id = @id
            RETURNING
                id, war_id, region_id, source_map_name, source_region_id,
                first_seen_at, last_seen_at, created_at;
            """;

        AddUuid(update, "id", existing.Id.Value);
        AddNullableInteger(
            update,
            "source_region_id",
            effectiveSourceRegionId);
        AddTimestamp(update, "first_seen_at", firstSeenAt);
        AddTimestamp(update, "last_seen_at", lastSeenAt);

        return await ReadWarRegionAsync(
            update,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "Coverage war-region disappeared during update.");
    }

    private static async Task<CoverageReprocessingRunDescriptor>
        RecordCoverageRunAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CoverageObservationId coverageObservationId,
            string processorVersion,
            string outcome,
            string? errorCode,
            DateTimeOffset startedAt,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO evidence.coverage_reprocessing_runs
                (id, coverage_observation_id, processor_version,
                 outcome, error_code, started_at, completed_at)
            VALUES
                (@id, @coverage_observation_id, @processor_version,
                 @outcome, @error_code, @started_at, @completed_at)
            ON CONFLICT (coverage_observation_id, processor_version)
            DO NOTHING
            RETURNING
                id, coverage_observation_id, processor_version,
                outcome, error_code, started_at, completed_at, created_at;
            """;

        AddUuid(
            insert,
            "id",
            CoverageReprocessingRunId.New().Value);
        AddUuid(
            insert,
            "coverage_observation_id",
            coverageObservationId.Value);
        AddText(insert, "processor_version", processorVersion);
        AddText(insert, "outcome", outcome);
        AddNullableText(insert, "error_code", errorCode);
        AddTimestamp(insert, "started_at", startedAt);
        AddTimestamp(insert, "completed_at", completedAt);

        var created = await ReadCoverageRunAsync(
            insert,
            cancellationToken);
        if (created is not null)
        {
            return created;
        }

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            """
            SELECT
                id, coverage_observation_id, processor_version,
                outcome, error_code, started_at, completed_at, created_at
            FROM evidence.coverage_reprocessing_runs
            WHERE coverage_observation_id = @coverage_observation_id
              AND processor_version = @processor_version;
            """;

        AddUuid(
            select,
            "coverage_observation_id",
            coverageObservationId.Value);
        AddText(select, "processor_version", processorVersion);

        var existing = await ReadCoverageRunAsync(
            select,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "Coverage reprocessing uniqueness conflict was not readable.");

        if (!string.Equals(existing.Outcome, outcome, StringComparison.Ordinal) ||
            !string.Equals(existing.ErrorCode, errorCode, StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Repeated coverage reprocessing result differs from durable terminal outcome.");
        }

        return existing;
    }

    private static async Task<CoverageObservationDescriptor?>
        GetCoverageForUpdateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CoverageObservationId id,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                id, endpoint_id, collection_job_id, attempt_id,
                validation_fetch_id, representation_fetch_id,
                source_parse_run_id, state, boundary_at, detail_code,
                recorded_at
            FROM evidence.coverage_observations
            WHERE id = @id
            FOR UPDATE;
            """;
        AddUuid(command, "id", id.Value);
        return await ReadCoverageAsync(command, cancellationToken);
    }

    private static async Task<CoverageObservationDescriptor?>
        GetCoverageByAttemptAsync(
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
                id, endpoint_id, collection_job_id, attempt_id,
                validation_fetch_id, representation_fetch_id,
                source_parse_run_id, state, boundary_at, detail_code,
                recorded_at
            FROM evidence.coverage_observations
            WHERE attempt_id = @attempt_id;
            """;
        AddUuid(command, "attempt_id", attemptId.Value);
        return await ReadCoverageAsync(command, cancellationToken);
    }

    private static async Task<CoverageObservationDescriptor?>
        ReadCoverageAsync(
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

        return ReadCoverageColumns(reader, 0);
    }

    private static CoverageObservationDescriptor ReadCoverageColumns(
        NpgsqlDataReader reader,
        int offset) =>
        new(
            new CoverageObservationId(reader.GetGuid(offset)),
            new EndpointId(reader.GetGuid(offset + 1)),
            new CollectionJobId(reader.GetGuid(offset + 2)),
            new IngestionAttemptId(reader.GetGuid(offset + 3)),
            reader.IsDBNull(offset + 4)
                ? null
                : new FetchId(reader.GetGuid(offset + 4)),
            reader.IsDBNull(offset + 5)
                ? null
                : new FetchId(reader.GetGuid(offset + 5)),
            reader.IsDBNull(offset + 6)
                ? null
                : new SourceParseRunId(reader.GetGuid(offset + 6)),
            ParseState(reader.GetString(offset + 7)),
            reader.GetFieldValue<DateTimeOffset>(offset + 8),
            reader.IsDBNull(offset + 9)
                ? null
                : reader.GetString(offset + 9),
            reader.GetFieldValue<DateTimeOffset>(offset + 10));

    private static async Task<CoverageReprocessingRunDescriptor?>
        ReadCoverageRunAsync(
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

        return new CoverageReprocessingRunDescriptor(
            new CoverageReprocessingRunId(reader.GetGuid(0)),
            new CoverageObservationId(reader.GetGuid(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));
    }

    private static async Task<RegionDescriptor?> ReadRegionAsync(
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

        return new RegionDescriptor(
            new RegionId(reader.GetGuid(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3));
    }

    private static async Task<WarRegionDescriptor?> ReadWarRegionAsync(
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

        return new WarRegionDescriptor(
            new WarRegionId(reader.GetGuid(0)),
            new WarId(reader.GetGuid(1)),
            new RegionId(reader.GetGuid(2)),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));
    }

    private static void EnsureEquivalent(
        CoverageObservationDescriptor existing,
        CoverageObservationWrite supplied)
    {
        if (existing.EndpointId != supplied.EndpointId ||
            existing.CollectionJobId != supplied.CollectionJobId ||
            existing.AttemptId != supplied.AttemptId ||
            existing.ValidationFetchId != supplied.ValidationFetchId ||
            existing.RepresentationFetchId !=
                supplied.RepresentationFetchId ||
            existing.SourceParseRunId != supplied.SourceParseRunId ||
            existing.State != supplied.State ||
            existing.BoundaryAt != supplied.BoundaryAt ||
            !string.Equals(
                existing.DetailCode,
                supplied.DetailCode,
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Repeated coverage input differs from durable coverage.");
        }
    }

    private static void ValidateCoverageWrite(CoverageObservationWrite write)
    {
        ValidateId(write.EndpointId.Value, nameof(write.EndpointId));
        ValidateId(
            write.CollectionJobId.Value,
            nameof(write.CollectionJobId));
        ValidateId(write.AttemptId.Value, nameof(write.AttemptId));

        if (write.ValidationFetchId is { } validation)
        {
            ValidateId(validation.Value, nameof(write.ValidationFetchId));
        }

        if (write.RepresentationFetchId is { } representation)
        {
            ValidateId(
                representation.Value,
                nameof(write.RepresentationFetchId));
        }

        if (write.SourceParseRunId is { } parse)
        {
            ValidateId(parse.Value, nameof(write.SourceParseRunId));
        }

        if (write.DetailCode is { Length: > 128 } ||
            write.DetailCode is not null &&
            !string.Equals(
                write.DetailCode,
                write.DetailCode.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Coverage detail code must be trimmed and at most 128 characters.",
                nameof(write));
        }
    }

    private static void ValidateContinuityWrite(CoverageContinuityWrite write)
    {
        ValidateId(
            write.CoverageObservationId.Value,
            nameof(write.CoverageObservationId));
        ValidateId(write.ShardId.Value, nameof(write.ShardId));
        ValidateId(write.WarId.Value, nameof(write.WarId));
        ValidateRequiredText(
            write.ProcessorVersion,
            128,
            nameof(write.ProcessorVersion));
        ArgumentNullException.ThrowIfNull(write.Memberships);

        if (write.CompletedAt < write.StartedAt)
        {
            throw new ArgumentException(
                "Coverage processing completion must not precede start.",
                nameof(write));
        }

        var sourceNames = new HashSet<string>(StringComparer.Ordinal);
        var canonicalKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var membership in write.Memberships)
        {
            ArgumentNullException.ThrowIfNull(membership);
            ValidateRequiredText(
                membership.CanonicalKey,
                256,
                nameof(membership.CanonicalKey));
            ValidateRequiredText(
                membership.DisplayName,
                256,
                nameof(membership.DisplayName));
            ValidateRequiredText(
                membership.SourceMapName,
                256,
                nameof(membership.SourceMapName));

            if (membership.SourceRegionId is < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(write),
                    "SourceRegionId must not be negative.");
            }

            if (!sourceNames.Add(membership.SourceMapName) ||
                !canonicalKeys.Add(membership.CanonicalKey))
            {
                throw new ArgumentException(
                    "Coverage continuity memberships must be unique.",
                    nameof(write));
            }
        }
    }

    private static string ToStorage(CoverageState state) =>
        state switch
        {
            CoverageState.Observed => "observed",
            CoverageState.SourceNotModified => "source_not_modified",
            CoverageState.SourceUnavailable => "source_unavailable",
            CoverageState.CollectorUnavailable => "collector_unavailable",
            CoverageState.Rejected => "rejected",
            CoverageState.Unknown => "unknown",
            _ => throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "Unknown coverage state."),
        };

    private static CoverageState ParseState(string value) =>
        value switch
        {
            "observed" => CoverageState.Observed,
            "source_not_modified" => CoverageState.SourceNotModified,
            "source_unavailable" => CoverageState.SourceUnavailable,
            "collector_unavailable" => CoverageState.CollectorUnavailable,
            "rejected" => CoverageState.Rejected,
            "unknown" => CoverageState.Unknown,
            _ => throw new CanonicalStateIntegrityException(
                $"Unknown durable coverage state '{value}'."),
        };

    private static void ValidateCapabilityPlans(
        IReadOnlyList<CoverageCapabilityPlan> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (capabilities.Count is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capabilities),
                "Coverage capability plan must contain between 1 and 128 entries.");
        }

        var capabilityKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            ArgumentNullException.ThrowIfNull(capability);
            ValidateRequiredText(
                capability.CapabilityKey,
                128,
                nameof(capability.CapabilityKey));
            ValidateRequiredText(
                capability.ParserVersion,
                128,
                nameof(capability.ParserVersion));
            ValidateRequiredText(
                capability.NormalizerVersion,
                128,
                nameof(capability.NormalizerVersion));

            if (capability.DependencyRank is < 0 or > 1024)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capabilities),
                    "Coverage dependency rank must be between 0 and 1024.");
            }

            if (!capabilityKeys.Add(capability.CapabilityKey))
            {
                throw new ArgumentException(
                    $"Coverage capability plan contains duplicate capability '{capability.CapabilityKey}'.",
                    nameof(capabilities));
            }
        }
    }

    private static void AddCapabilityPlan(
        NpgsqlCommand command,
        IReadOnlyList<CoverageCapabilityPlan> capabilities)
    {
        command.Parameters.Add(
            "capability_keys",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            capabilities.Select(item => item.CapabilityKey).ToArray();
        command.Parameters.Add(
            "parser_versions",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            capabilities.Select(item => item.ParserVersion).ToArray();
        command.Parameters.Add(
            "normalizer_versions",
            NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            capabilities.Select(item => item.NormalizerVersion).ToArray();
        command.Parameters.Add(
            "dependency_ranks",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            capabilities.Select(item => item.DependencyRank).ToArray();
    }

    private static void ValidateBatchSize(int batchSize)
    {
        if (batchSize is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                batchSize,
                "Coverage batch size must be between 1 and 1024.");
        }
    }

    private static void ValidateRequiredText(
        string value,
        int maximum,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximum ||
            !string.Equals(
                value,
                value.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{parameterName} must be non-empty, already trimmed, and at most {maximum} characters.",
                parameterName);
        }
    }

    private static void ValidateId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Identifier must not be empty.",
                parameterName);
        }
    }

    private static void AddUuid(
        NpgsqlCommand command,
        string name,
        Guid value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = value;

    private static void AddNullableUuid(
        NpgsqlCommand command,
        string name,
        Guid? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value =
            value is null ? DBNull.Value : value.Value;

    private static void AddText(
        NpgsqlCommand command,
        string name,
        string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value;

    private static void AddNullableText(
        NpgsqlCommand command,
        string name,
        string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value =
            value is null ? DBNull.Value : value;

    private static void AddTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value =
            value;

    private static void AddNullableInteger(
        NpgsqlCommand command,
        string name,
        int? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Integer).Value =
            value is null ? DBNull.Value : value.Value;
}
