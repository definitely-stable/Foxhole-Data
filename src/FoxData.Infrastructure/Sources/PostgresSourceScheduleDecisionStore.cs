using System.Data;
using FoxData.Application.Sources;
using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Sources;

public sealed class PostgresSourceScheduleDecisionStore(
    NpgsqlDataSource dataSource)
    : ISourceScheduleDecisionStore
{
    private const string Columns =
        """
        fetch_id, endpoint_id, policy_version, effective_cadence_ms,
        endpoint_active, probe_selected, source_cache_eligible_at,
        next_target_at, retry_eligible_at, successor_job_id,
        successor_available_at, created_at
        """;

    public async Task<SourceScheduleDecisionDescriptor?> GetAsync(
        FetchId fetchId,
        CancellationToken cancellationToken)
    {
        EnsureFetchId(fetchId);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        return await GetAsync(
            connection,
            transaction: null,
            fetchId,
            cancellationToken);
    }

    public async Task<SourceScheduleDecisionDescriptor> RecordAsync(
        SourceScheduleDecisionWrite decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        Validate(decision);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        await ValidateReferencesAsync(
            connection,
            transaction,
            decision,
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            INSERT INTO evidence.source_schedule_decisions
                (fetch_id, endpoint_id, policy_version, effective_cadence_ms,
                 endpoint_active, probe_selected, source_cache_eligible_at,
                 next_target_at, retry_eligible_at, successor_job_id,
                 successor_available_at)
            VALUES
                (@fetch_id, @endpoint_id, @policy_version, @effective_cadence_ms,
                 @endpoint_active, @probe_selected, @source_cache_eligible_at,
                 @next_target_at, @retry_eligible_at, @successor_job_id,
                 @successor_available_at)
            ON CONFLICT (fetch_id)
            DO NOTHING
            RETURNING {Columns};
            """;

        AddUuid(command, "fetch_id", decision.FetchId.Value);
        AddUuid(command, "endpoint_id", decision.EndpointId.Value);
        AddText(command, "policy_version", decision.PolicyVersion);
        AddBigint(
            command,
            "effective_cadence_ms",
            decision.EffectiveCadenceMs);
        AddBoolean(
            command,
            "endpoint_active",
            decision.EndpointActive);
        AddBoolean(
            command,
            "probe_selected",
            decision.ProbeSelected);
        AddNullableTimestamp(
            command,
            "source_cache_eligible_at",
            decision.SourceCacheEligibleAt);
        AddNullableTimestamp(
            command,
            "next_target_at",
            decision.NextTargetAt);
        AddNullableTimestamp(
            command,
            "retry_eligible_at",
            decision.RetryEligibleAt);
        AddNullableUuid(
            command,
            "successor_job_id",
            decision.SuccessorJobId?.Value);
        AddNullableTimestamp(
            command,
            "successor_available_at",
            decision.SuccessorAvailableAt);

        var inserted = await ReadAsync(
            command,
            cancellationToken);

        if (inserted is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return inserted;
        }

        var existing = await GetAsync(
            connection,
            transaction,
            decision.FetchId,
            cancellationToken)
            ?? throw new SourceStateIntegrityException(
                "Scheduling-decision uniqueness conflict was observed but the existing row was not readable.");

        EnsureEquivalent(existing, decision);

        await transaction.CommitAsync(cancellationToken);
        return existing;
    }

    private static async Task<SourceScheduleDecisionDescriptor?> GetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        FetchId fetchId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {Columns}
            FROM evidence.source_schedule_decisions
            WHERE fetch_id = @fetch_id;
            """;
        AddUuid(command, "fetch_id", fetchId.Value);

        return await ReadAsync(
            command,
            cancellationToken);
    }

    private static async Task<SourceScheduleDecisionDescriptor?> ReadAsync(
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

        return new SourceScheduleDecisionDescriptor(
            new FetchId(reader.GetGuid(0)),
            new EndpointId(reader.GetGuid(1)),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.IsDBNull(6)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9)
                ? null
                : new CollectionJobId(reader.GetGuid(9)),
            reader.IsDBNull(10)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(10),
            reader.GetFieldValue<DateTimeOffset>(11));
    }

    private static async Task ValidateReferencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SourceScheduleDecisionWrite decision,
        CancellationToken cancellationToken)
    {
        await using (var fetch = connection.CreateCommand())
        {
            fetch.Transaction = transaction;
            fetch.CommandText =
                """
                SELECT endpoint_id
                FROM evidence.fetches
                WHERE id = @fetch_id;
                """;
            AddUuid(fetch, "fetch_id", decision.FetchId.Value);

            var endpoint = await fetch.ExecuteScalarAsync(
                cancellationToken);
            if (endpoint is not Guid endpointId)
            {
                throw new SourceStateIntegrityException(
                    $"Scheduling decision references missing fetch {decision.FetchId}.");
            }

            if (endpointId != decision.EndpointId.Value)
            {
                throw new SourceStateIntegrityException(
                    "Scheduling decision fetch belongs to a different endpoint.");
            }
        }

        if (decision.SuccessorJobId is null)
        {
            return;
        }

        await using var successor = connection.CreateCommand();
        successor.Transaction = transaction;
        successor.CommandText =
            """
            SELECT endpoint_id
            FROM ingest.collection_jobs
            WHERE id = @job_id;
            """;
        AddUuid(
            successor,
            "job_id",
            decision.SuccessorJobId.Value.Value);

        var successorEndpoint = await successor.ExecuteScalarAsync(
            cancellationToken);
        if (successorEndpoint is not Guid successorEndpointId)
        {
            throw new SourceStateIntegrityException(
                $"Scheduling decision references missing successor job {decision.SuccessorJobId}.");
        }

        if (successorEndpointId != decision.EndpointId.Value)
        {
            throw new SourceStateIntegrityException(
                "Scheduling decision successor belongs to a different endpoint.");
        }
    }

    private static void EnsureEquivalent(
        SourceScheduleDecisionDescriptor existing,
        SourceScheduleDecisionWrite supplied)
    {
        if (existing.FetchId != supplied.FetchId ||
            existing.EndpointId != supplied.EndpointId ||
            !string.Equals(
                existing.PolicyVersion,
                supplied.PolicyVersion,
                StringComparison.Ordinal) ||
            existing.EffectiveCadenceMs !=
                supplied.EffectiveCadenceMs ||
            existing.EndpointActive != supplied.EndpointActive ||
            existing.ProbeSelected != supplied.ProbeSelected ||
            existing.SourceCacheEligibleAt !=
                supplied.SourceCacheEligibleAt ||
            existing.NextTargetAt != supplied.NextTargetAt ||
            existing.RetryEligibleAt != supplied.RetryEligibleAt ||
            existing.SuccessorJobId != supplied.SuccessorJobId ||
            existing.SuccessorAvailableAt !=
                supplied.SuccessorAvailableAt)
        {
            throw new SourceStateIntegrityException(
                "Repeated scheduling-decision input differs from the durable decision.");
        }
    }

    private static void Validate(
        SourceScheduleDecisionWrite decision)
    {
        EnsureFetchId(decision.FetchId);

        if (decision.EndpointId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Endpoint identifier must not be empty.",
                nameof(decision));
        }

        if (string.IsNullOrWhiteSpace(decision.PolicyVersion) ||
            decision.PolicyVersion.Length > 128 ||
            !string.Equals(
                decision.PolicyVersion,
                decision.PolicyVersion.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "PolicyVersion must be non-empty, already trimmed, and at most 128 characters.",
                nameof(decision));
        }

        if (decision.EffectiveCadenceMs <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(decision),
                decision.EffectiveCadenceMs,
                "EffectiveCadenceMs must be positive.");
        }

        if ((decision.SuccessorJobId is null) !=
            (decision.SuccessorAvailableAt is null))
        {
            throw new ArgumentException(
                "SuccessorJobId and SuccessorAvailableAt must either both be present or both be absent.",
                nameof(decision));
        }

        if (!decision.EndpointActive &&
            decision.SuccessorJobId is not null)
        {
            throw new ArgumentException(
                "Inactive endpoints must not have a successor job.",
                nameof(decision));
        }
    }

    private static void EnsureFetchId(FetchId fetchId)
    {
        if (fetchId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Fetch identifier must not be empty.",
                nameof(fetchId));
        }
    }

    private static void AddUuid(
        NpgsqlCommand command,
        string name,
        Guid value) =>
        command.Parameters.Add(
            name,
            NpgsqlDbType.Uuid).Value = value;

    private static void AddNullableUuid(
        NpgsqlCommand command,
        string name,
        Guid? value) =>
        command.Parameters.Add(
            name,
            NpgsqlDbType.Uuid).Value =
            value is null
                ? DBNull.Value
                : value.Value;

    private static void AddText(
        NpgsqlCommand command,
        string name,
        string value) =>
        command.Parameters.Add(
            name,
            NpgsqlDbType.Text).Value = value;

    private static void AddBigint(
        NpgsqlCommand command,
        string name,
        long value) =>
        command.Parameters.Add(
            name,
            NpgsqlDbType.Bigint).Value = value;

    private static void AddBoolean(
        NpgsqlCommand command,
        string name,
        bool value) =>
        command.Parameters.Add(
            name,
            NpgsqlDbType.Boolean).Value = value;

    private static void AddNullableTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset? value) =>
        command.Parameters.Add(
            name,
            NpgsqlDbType.TimestampTz).Value =
            value is null
                ? DBNull.Value
                : value.Value;
}
