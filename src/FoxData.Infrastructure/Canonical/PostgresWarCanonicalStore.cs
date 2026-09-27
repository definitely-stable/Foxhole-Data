using System.Data;
using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Core.Runtime;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Canonical;

public sealed class PostgresWarCanonicalStore(NpgsqlDataSource dataSource)
    : IWarCanonicalStore
{
    private const string NormalizationColumns =
        """
        id, source_parse_run_id, normalizer_version, outcome, error_code,
        started_at, completed_at, created_at
        """;

    public async Task<WarCanonicalResult> RecordAcceptedAsync(
        WarCanonicalWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        Validate(write);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        await EnsureParseProvenanceAsync(
            connection,
            transaction,
            write,
            cancellationToken);

        var normalizationRun = await RecordNormalizationRunAsync(
            connection,
            transaction,
            write,
            cancellationToken);

        var war = await GetOrCreateWarAsync(
            connection,
            transaction,
            write,
            cancellationToken);

        var observation = await RecordObservationAsync(
            connection,
            transaction,
            war,
            normalizationRun,
            write,
            cancellationToken);

        war = await RefreshWarProjectionAsync(
            connection,
            transaction,
            war.Id,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new WarCanonicalResult(
            war,
            observation,
            normalizationRun);
    }

    private static async Task EnsureParseProvenanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarCanonicalWrite write,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                parse_run.representation_fetch_id,
                parse_run.outcome,
                shard.id
            FROM evidence.source_parse_runs AS parse_run
            INNER JOIN evidence.fetches AS fetch
                ON fetch.id = parse_run.representation_fetch_id
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = fetch.endpoint_id
            INNER JOIN sources.shards AS shard
                ON shard.id = endpoint.shard_id
            WHERE parse_run.id = @source_parse_run_id;
            """;

        AddUuid(command, "source_parse_run_id", write.SourceParseRunId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CanonicalStateIntegrityException(
                $"Source parse run {write.SourceParseRunId} does not exist.");
        }

        var representationFetchId = new FetchId(reader.GetGuid(0));
        var parseOutcome = reader.GetString(1);
        var shardId = new ShardId(reader.GetGuid(2));

        if (representationFetchId != write.RepresentationFetchId ||
            shardId != write.ShardId)
        {
            throw new CanonicalStateIntegrityException(
                "War canonical write does not match its durable source-parse provenance.");
        }

        if (parseOutcome is not ("parsed" or "parsed_with_unknowns"))
        {
            throw new CanonicalStateIntegrityException(
                $"Source parse run {write.SourceParseRunId} is not a successful parse.");
        }
    }

    private static async Task<NormalizationRunDescriptor> RecordNormalizationRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarCanonicalWrite write,
        CancellationToken cancellationToken)
    {
        var proposedId = NormalizationRunId.New();

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            $"""
            INSERT INTO evidence.normalization_runs
                (id, source_parse_run_id, normalizer_version, outcome, error_code,
                 started_at, completed_at)
            VALUES
                (@id, @source_parse_run_id, @normalizer_version, 'normalized', NULL,
                 @started_at, @completed_at)
            ON CONFLICT (source_parse_run_id, normalizer_version)
            DO NOTHING
            RETURNING {NormalizationColumns};
            """;

        AddUuid(insert, "id", proposedId.Value);
        AddUuid(insert, "source_parse_run_id", write.SourceParseRunId.Value);
        AddText(insert, "normalizer_version", write.NormalizerVersion);
        AddTimestamp(insert, "started_at", write.NormalizationStartedAt);
        AddTimestamp(insert, "completed_at", write.NormalizationCompletedAt);

        var created = await ReadNormalizationRunAsync(
            insert,
            cancellationToken);

        if (created is not null)
        {
            return created;
        }

        var existing = await GetNormalizationRunAsync(
            connection,
            transaction,
            write.SourceParseRunId,
            write.NormalizerVersion,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "Normalization-run uniqueness conflict was observed but the existing row was not readable.");

        if (existing.Outcome != NormalizationRunOutcome.Normalized ||
            existing.ErrorCode is not null)
        {
            throw new CanonicalStateIntegrityException(
                "Repeated accepted war normalization conflicts with the durable normalization outcome.");
        }

        return existing;
    }

    private static async Task<NormalizationRunDescriptor?> GetNormalizationRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SourceParseRunId sourceParseRunId,
        string normalizerVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {NormalizationColumns}
            FROM evidence.normalization_runs
            WHERE source_parse_run_id = @source_parse_run_id
              AND normalizer_version = @normalizer_version;
            """;

        AddUuid(command, "source_parse_run_id", sourceParseRunId.Value);
        AddText(command, "normalizer_version", normalizerVersion);

        return await ReadNormalizationRunAsync(
            command,
            cancellationToken);
    }

    private static async Task<NormalizationRunDescriptor?> ReadNormalizationRunAsync(
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

        return new NormalizationRunDescriptor(
            new NormalizationRunId(reader.GetGuid(0)),
            new SourceParseRunId(reader.GetGuid(1)),
            reader.GetString(2),
            ParseNormalizationOutcome(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));
    }

    private static NormalizationRunOutcome ParseNormalizationOutcome(
        string value) =>
        value switch
        {
            "normalized" => NormalizationRunOutcome.Normalized,
            "rejected" => NormalizationRunOutcome.Rejected,
            "failed" => NormalizationRunOutcome.Failed,
            _ => throw new CanonicalStateIntegrityException(
                $"Unknown durable normalization outcome '{value}'."),
        };

    private static async Task<WarDescriptor> GetOrCreateWarAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarCanonicalWrite write,
        CancellationToken cancellationToken)
    {
        var proposedId = WarId.New();

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO runtime.wars
                    (id, shard_id, source_war_id, war_number,
                     first_observed_at, last_observed_at)
                VALUES
                    (@id, @shard_id, @source_war_id, @war_number,
                     @observed_at, @observed_at)
                ON CONFLICT (shard_id, source_war_id)
                DO NOTHING
                RETURNING
                    id, shard_id, source_war_id, war_number,
                    first_observed_at, last_observed_at, created_at;
                """;

            AddUuid(insert, "id", proposedId.Value);
            AddUuid(insert, "shard_id", write.ShardId.Value);
            AddText(insert, "source_war_id", write.Snapshot.SourceWarId);
            AddNullableInteger(insert, "war_number", write.Snapshot.WarNumber);
            AddTimestamp(insert, "observed_at", write.ObservedAt);

            var created = await ReadWarAsync(insert, cancellationToken);
            if (created is not null)
            {
                return created;
            }
        }

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            """
            SELECT
                id, shard_id, source_war_id, war_number,
                first_observed_at, last_observed_at, created_at
            FROM runtime.wars
            WHERE shard_id = @shard_id
              AND source_war_id = @source_war_id
            FOR UPDATE;
            """;

        AddUuid(select, "shard_id", write.ShardId.Value);
        AddText(select, "source_war_id", write.Snapshot.SourceWarId);

        return await ReadWarAsync(select, cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "War uniqueness conflict was observed but the existing row was not readable.");
    }

    private static async Task<WarObservationDescriptor> RecordObservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarDescriptor war,
        NormalizationRunDescriptor normalizationRun,
        WarCanonicalWrite write,
        CancellationToken cancellationToken)
    {
        var proposedId = WarObservationId.New();

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO runtime.war_observations
                (id, war_id, normalization_run_id, representation_fetch_id,
                 observed_at, war_number, winner,
                 conquest_start_time, conquest_end_time, resistance_start_time,
                 scheduled_conquest_end_time, required_victory_towns,
                 short_required_victory_towns)
            VALUES
                (@id, @war_id, @normalization_run_id, @representation_fetch_id,
                 @observed_at, @war_number, @winner,
                 @conquest_start_time, @conquest_end_time, @resistance_start_time,
                 @scheduled_conquest_end_time, @required_victory_towns,
                 @short_required_victory_towns)
            ON CONFLICT (normalization_run_id)
            DO NOTHING
            RETURNING
                id, war_id, normalization_run_id, representation_fetch_id,
                observed_at, recorded_at, war_number, winner,
                conquest_start_time, conquest_end_time, resistance_start_time,
                scheduled_conquest_end_time, required_victory_towns,
                short_required_victory_towns;
            """;

        AddUuid(insert, "id", proposedId.Value);
        AddUuid(insert, "war_id", war.Id.Value);
        AddUuid(insert, "normalization_run_id", normalizationRun.Id.Value);
        AddUuid(insert, "representation_fetch_id", write.RepresentationFetchId.Value);
        AddTimestamp(insert, "observed_at", write.ObservedAt);
        AddNullableInteger(insert, "war_number", write.Snapshot.WarNumber);
        AddNullableText(insert, "winner", write.Snapshot.Winner);
        AddNullableTimestamp(insert, "conquest_start_time", write.Snapshot.ConquestStartTime);
        AddNullableTimestamp(insert, "conquest_end_time", write.Snapshot.ConquestEndTime);
        AddNullableTimestamp(insert, "resistance_start_time", write.Snapshot.ResistanceStartTime);
        AddNullableTimestamp(
            insert,
            "scheduled_conquest_end_time",
            write.Snapshot.ScheduledConquestEndTime);
        AddNullableInteger(
            insert,
            "required_victory_towns",
            write.Snapshot.RequiredVictoryTowns);
        AddNullableInteger(
            insert,
            "short_required_victory_towns",
            write.Snapshot.ShortRequiredVictoryTowns);

        var created = await ReadObservationAsync(insert, cancellationToken);
        if (created is not null)
        {
            return created;
        }

        var existing = await GetObservationByNormalizationRunAsync(
            connection,
            transaction,
            normalizationRun.Id,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "War-observation uniqueness conflict was observed but the existing row was not readable.");

        EnsureEquivalent(existing, war, normalizationRun, write);
        return existing;
    }

    private static async Task<WarDescriptor> RefreshWarProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarId warId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            WITH bounds AS
            (
                SELECT
                    MIN(observed_at) AS first_observed_at,
                    MAX(observed_at) AS last_observed_at
                FROM runtime.war_observations
                WHERE war_id = @war_id
            ),
            latest AS
            (
                SELECT war_number
                FROM runtime.war_observations
                WHERE war_id = @war_id
                ORDER BY observed_at DESC, representation_fetch_id DESC
                LIMIT 1
            )
            UPDATE runtime.wars AS war
            SET
                war_number = latest.war_number,
                first_observed_at = bounds.first_observed_at,
                last_observed_at = bounds.last_observed_at
            FROM bounds, latest
            WHERE war.id = @war_id
            RETURNING
                war.id, war.shard_id, war.source_war_id, war.war_number,
                war.first_observed_at, war.last_observed_at, war.created_at;
            """;

        AddUuid(command, "war_id", warId.Value);

        return await ReadWarAsync(command, cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "War projection could not be rebuilt from its durable observations.");
    }

    private static async Task<WarObservationDescriptor?> GetObservationByNormalizationRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationRunId normalizationRunId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                id, war_id, normalization_run_id, representation_fetch_id,
                observed_at, recorded_at, war_number, winner,
                conquest_start_time, conquest_end_time, resistance_start_time,
                scheduled_conquest_end_time, required_victory_towns,
                short_required_victory_towns
            FROM runtime.war_observations
            WHERE normalization_run_id = @normalization_run_id;
            """;

        AddUuid(command, "normalization_run_id", normalizationRunId.Value);
        return await ReadObservationAsync(command, cancellationToken);
    }

    private static async Task<WarDescriptor?> ReadWarAsync(
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

        return new WarDescriptor(
            new WarId(reader.GetGuid(0)),
            new ShardId(reader.GetGuid(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6));
    }

    private static async Task<WarObservationDescriptor?> ReadObservationAsync(
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

        return new WarObservationDescriptor(
            new WarObservationId(reader.GetGuid(0)),
            new WarId(reader.GetGuid(1)),
            new NormalizationRunId(reader.GetGuid(2)),
            new FetchId(reader.GetGuid(3)),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            reader.IsDBNull(12) ? null : reader.GetInt32(12),
            reader.IsDBNull(13) ? null : reader.GetInt32(13));
    }

    private static void EnsureEquivalent(
        WarObservationDescriptor existing,
        WarDescriptor war,
        NormalizationRunDescriptor normalizationRun,
        WarCanonicalWrite supplied)
    {
        var snapshot = supplied.Snapshot;

        if (existing.WarId != war.Id ||
            existing.NormalizationRunId != normalizationRun.Id ||
            existing.RepresentationFetchId != supplied.RepresentationFetchId ||
            existing.ObservedAt != supplied.ObservedAt ||
            existing.WarNumber != snapshot.WarNumber ||
            !string.Equals(existing.Winner, snapshot.Winner, StringComparison.Ordinal) ||
            existing.ConquestStartTime != snapshot.ConquestStartTime ||
            existing.ConquestEndTime != snapshot.ConquestEndTime ||
            existing.ResistanceStartTime != snapshot.ResistanceStartTime ||
            existing.ScheduledConquestEndTime != snapshot.ScheduledConquestEndTime ||
            existing.RequiredVictoryTowns != snapshot.RequiredVictoryTowns ||
            existing.ShortRequiredVictoryTowns != snapshot.ShortRequiredVictoryTowns)
        {
            throw new CanonicalStateIntegrityException(
                "Repeated war canonical write differs from the durable observation.");
        }
    }

    private static void Validate(WarCanonicalWrite write)
    {
        ValidateId(write.SourceParseRunId.Value, nameof(write.SourceParseRunId));
        ValidateId(write.ShardId.Value, nameof(write.ShardId));
        ValidateId(write.RepresentationFetchId.Value, nameof(write.RepresentationFetchId));

        if (string.IsNullOrWhiteSpace(write.NormalizerVersion) ||
            write.NormalizerVersion.Length > 128 ||
            !string.Equals(
                write.NormalizerVersion,
                write.NormalizerVersion.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "NormalizerVersion must be non-empty, already trimmed, and at most 128 characters.",
                nameof(write));
        }

        if (write.NormalizationCompletedAt < write.NormalizationStartedAt)
        {
            throw new ArgumentException(
                "NormalizationCompletedAt must not be earlier than NormalizationStartedAt.",
                nameof(write));
        }

        if (string.IsNullOrWhiteSpace(write.Snapshot.SourceWarId) ||
            write.Snapshot.SourceWarId.Length > 256 ||
            !string.Equals(
                write.Snapshot.SourceWarId,
                write.Snapshot.SourceWarId.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "SourceWarId must be non-empty, already trimmed, and at most 256 characters.",
                nameof(write));
        }

        if (write.Snapshot.Winner is { Length: > 128 })
        {
            throw new ArgumentException(
                "Winner must be at most 128 characters.",
                nameof(write));
        }

        if (write.Snapshot.WarNumber is < 0 ||
            write.Snapshot.RequiredVictoryTowns is < 0 ||
            write.Snapshot.ShortRequiredVictoryTowns is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(write),
                "War number and victory-town counts must not be negative.");
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

    private static void AddNullableInteger(
        NpgsqlCommand command,
        string name,
        int? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Integer).Value =
            value is null ? DBNull.Value : value.Value;

    private static void AddTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value = value;

    private static void AddNullableTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset? value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value =
            value is null ? DBNull.Value : value.Value;
}
