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
    public async Task<(WarDescriptor War, WarObservationDescriptor Observation)> RecordAsync(
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

        await EnsureProvenanceAsync(
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
            write,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return (war, observation);
    }

    private static async Task EnsureProvenanceAsync(
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
                normalization.source_parse_run_id,
                normalization.outcome,
                parse_run.representation_fetch_id,
                shard.id
            FROM evidence.normalization_runs AS normalization
            INNER JOIN evidence.source_parse_runs AS parse_run
                ON parse_run.id = normalization.source_parse_run_id
            INNER JOIN evidence.fetches AS fetch
                ON fetch.id = parse_run.representation_fetch_id
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = fetch.endpoint_id
            INNER JOIN sources.shards AS shard
                ON shard.id = endpoint.shard_id
            WHERE normalization.id = @normalization_run_id;
            """;

        AddUuid(command, "normalization_run_id", write.NormalizationRunId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CanonicalStateIntegrityException(
                $"Normalization run {write.NormalizationRunId} does not exist.");
        }

        var sourceParseRunId = new SourceParseRunId(reader.GetGuid(0));
        var outcome = reader.GetString(1);
        var representationFetchId = new FetchId(reader.GetGuid(2));
        var shardId = new ShardId(reader.GetGuid(3));

        if (sourceParseRunId != write.SourceParseRunId ||
            representationFetchId != write.RepresentationFetchId ||
            shardId != write.ShardId ||
            !string.Equals(outcome, "normalized", StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "War canonical write does not match its durable normalization provenance.");
        }
    }

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

        WarDescriptor existing;
        await using (var select = connection.CreateCommand())
        {
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

            existing = await ReadWarAsync(select, cancellationToken)
                ?? throw new CanonicalStateIntegrityException(
                    "War uniqueness conflict was observed but the existing row was not readable.");
        }

        if (existing.WarNumber is { } existingNumber &&
            write.Snapshot.WarNumber is { } suppliedNumber &&
            existingNumber != suppliedNumber)
        {
            throw new CanonicalStateIntegrityException(
                $"Source war '{write.Snapshot.SourceWarId}' changed warNumber from {existingNumber} to {suppliedNumber}.");
        }

        var effectiveWarNumber = existing.WarNumber ?? write.Snapshot.WarNumber;
        var firstObservedAt =
            existing.FirstObservedAt <= write.ObservedAt
                ? existing.FirstObservedAt
                : write.ObservedAt;
        var lastObservedAt =
            existing.LastObservedAt >= write.ObservedAt
                ? existing.LastObservedAt
                : write.ObservedAt;

        if (effectiveWarNumber == existing.WarNumber &&
            firstObservedAt == existing.FirstObservedAt &&
            lastObservedAt == existing.LastObservedAt)
        {
            return existing;
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE runtime.wars
            SET
                war_number = @war_number,
                first_observed_at = @first_observed_at,
                last_observed_at = @last_observed_at
            WHERE id = @id
            RETURNING
                id, shard_id, source_war_id, war_number,
                first_observed_at, last_observed_at, created_at;
            """;

        AddUuid(update, "id", existing.Id.Value);
        AddNullableInteger(update, "war_number", effectiveWarNumber);
        AddTimestamp(update, "first_observed_at", firstObservedAt);
        AddTimestamp(update, "last_observed_at", lastObservedAt);

        return await ReadWarAsync(update, cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "War row disappeared while updating observation bounds.");
    }

    private static async Task<WarObservationDescriptor> RecordObservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarDescriptor war,
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
        AddUuid(insert, "normalization_run_id", write.NormalizationRunId.Value);
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
            write.NormalizationRunId,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "War-observation uniqueness conflict was observed but the existing row was not readable.");

        EnsureEquivalent(existing, war, write);
        return existing;
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
        WarCanonicalWrite supplied)
    {
        var snapshot = supplied.Snapshot;

        if (existing.WarId != war.Id ||
            existing.NormalizationRunId != supplied.NormalizationRunId ||
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
        ValidateId(write.NormalizationRunId.Value, nameof(write.NormalizationRunId));
        ValidateId(write.ShardId.Value, nameof(write.ShardId));
        ValidateId(write.RepresentationFetchId.Value, nameof(write.RepresentationFetchId));

        if (string.IsNullOrWhiteSpace(write.Snapshot.SourceWarId) ||
            write.Snapshot.SourceWarId.Length > 256)
        {
            throw new ArgumentException(
                "SourceWarId must be non-empty and at most 256 characters.",
                nameof(write));
        }

        if (write.Snapshot.Winner is { Length: > 128 })
        {
            throw new ArgumentException(
                "Winner must be at most 128 characters.",
                nameof(write));
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

    private static void AddInteger(
        NpgsqlCommand command,
        string name,
        int value) =>
        command.Parameters.Add(name, NpgsqlDbType.Integer).Value = value;

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
