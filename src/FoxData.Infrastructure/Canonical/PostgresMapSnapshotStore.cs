using System.Data;
using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Canonical;

public sealed class PostgresMapSnapshotStore(NpgsqlDataSource dataSource)
    : IMapSnapshotStore
{
    private const string NormalizationColumns =
        """
        id, source_parse_run_id, normalizer_version, outcome, error_code,
        started_at, completed_at, created_at
        """;

    private const string SnapshotColumns =
        """
        id, normalization_run_id, source_parse_run_id,
        representation_fetch_id, capability_kind, source_map_name,
        source_region_id, source_scorched_victory_towns,
        source_version, source_last_updated_ms, source_updated_at,
        source_map_items_array_present,
        source_map_text_items_array_present,
        item_count, text_item_count, recorded_at
        """;

    public async Task<MapSnapshotResult?> GetByNormalizationRunAsync(
        NormalizationRunId normalizationRunId,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);

        var normalizationRun = await GetNormalizationRunByIdAsync(
            connection,
            transaction: null,
            normalizationRunId,
            cancellationToken);
        if (normalizationRun is null)
        {
            return null;
        }

        var snapshot = await GetSnapshotByNormalizationRunAsync(
            connection,
            transaction: null,
            normalizationRunId,
            cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        var items = await GetItemsAsync(
            connection,
            transaction: null,
            snapshot.Id,
            cancellationToken);
        var textItems = await GetTextItemsAsync(
            connection,
            transaction: null,
            snapshot.Id,
            cancellationToken);

        return new MapSnapshotResult(
            normalizationRun,
            snapshot,
            items,
            textItems);
    }

    public async Task<MapSnapshotResult> RecordAcceptedAsync(
        MapSnapshotWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

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

        var snapshot = await RecordSnapshotAsync(
            connection,
            transaction,
            normalizationRun,
            write,
            cancellationToken);

        var existingItems = await GetItemsAsync(
            connection,
            transaction,
            snapshot.Id,
            cancellationToken);
        var existingTextItems = await GetTextItemsAsync(
            connection,
            transaction,
            snapshot.Id,
            cancellationToken);

        if (existingItems.Count == 0 &&
            existingTextItems.Count == 0 &&
            snapshot.ItemCount == write.Items.Count &&
            snapshot.TextItemCount == write.TextItems.Count)
        {
            await InsertItemsAsync(
                connection,
                transaction,
                snapshot.Id,
                write.Items,
                cancellationToken);
            await InsertTextItemsAsync(
                connection,
                transaction,
                snapshot.Id,
                write.TextItems,
                cancellationToken);

            existingItems = await GetItemsAsync(
                connection,
                transaction,
                snapshot.Id,
                cancellationToken);
            existingTextItems = await GetTextItemsAsync(
                connection,
                transaction,
                snapshot.Id,
                cancellationToken);
        }

        EnsureOccurrencesEquivalent(
            snapshot,
            existingItems,
            existingTextItems,
            write);

        await transaction.CommitAsync(cancellationToken);

        return new MapSnapshotResult(
            normalizationRun,
            snapshot,
            existingItems,
            existingTextItems);
    }

    private static async Task EnsureParseProvenanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MapSnapshotWrite write,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                parse_run.representation_fetch_id,
                parse_run.outcome,
                parse_run.capability_key,
                endpoint.capability_key,
                endpoint.semantic_key
            FROM evidence.source_parse_runs AS parse_run
            INNER JOIN evidence.fetches AS representation_fetch
                ON representation_fetch.id = parse_run.representation_fetch_id
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = representation_fetch.endpoint_id
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
        var outcome = reader.GetString(1);
        var parseCapabilityKey = reader.GetString(2);
        var endpointCapabilityKey = reader.GetString(3);
        var semanticKey = reader.GetString(4);

        if (representationFetchId != write.RepresentationFetchId ||
            !string.Equals(
                parseCapabilityKey,
                write.CapabilityKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                endpointCapabilityKey,
                write.CapabilityKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                parseCapabilityKey,
                endpointCapabilityKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                semanticKey,
                write.SemanticKey,
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Map snapshot write does not match its durable source-parse provenance.");
        }

        if (outcome is not ("parsed" or "parsed_with_unknowns"))
        {
            throw new CanonicalStateIntegrityException(
                $"Source parse run {write.SourceParseRunId} is not a successful parse.");
        }
    }

    private static async Task<NormalizationRunDescriptor> RecordNormalizationRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MapSnapshotWrite write,
        CancellationToken cancellationToken)
    {
        var proposedId = NormalizationRunId.New();

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            $"""
            INSERT INTO evidence.normalization_runs
                (id, source_parse_run_id, normalizer_version, outcome,
                 error_code, started_at, completed_at)
            VALUES
                (@id, @source_parse_run_id, @normalizer_version,
                 'normalized', NULL, @started_at, @completed_at)
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
                "Normalization-run uniqueness conflict was observed but no row was readable.");

        if (existing.Outcome != NormalizationRunOutcome.Normalized ||
            existing.ErrorCode is not null)
        {
            throw new CanonicalStateIntegrityException(
                "Accepted map normalization conflicts with the durable normalization outcome.");
        }

        return existing;
    }

    private static async Task<MapSnapshotDescriptor> RecordSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NormalizationRunDescriptor normalizationRun,
        MapSnapshotWrite write,
        CancellationToken cancellationToken)
    {
        var proposedId = MapSnapshotId.New();

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            $"""
            INSERT INTO evidence.map_snapshots
                (id, normalization_run_id, source_parse_run_id,
                 representation_fetch_id, capability_kind, source_map_name,
                 source_region_id, source_scorched_victory_towns,
                 source_version, source_last_updated_ms, source_updated_at,
                 source_map_items_array_present,
                 source_map_text_items_array_present,
                 item_count, text_item_count)
            VALUES
                (@id, @normalization_run_id, @source_parse_run_id,
                 @representation_fetch_id, @capability_kind, @source_map_name,
                 @source_region_id, @source_scorched_victory_towns,
                 @source_version, @source_last_updated_ms, @source_updated_at,
                 @source_map_items_array_present,
                 @source_map_text_items_array_present,
                 @item_count, @text_item_count)
            ON CONFLICT (normalization_run_id)
            DO NOTHING
            RETURNING {SnapshotColumns};
            """;

        AddUuid(insert, "id", proposedId.Value);
        AddUuid(insert, "normalization_run_id", normalizationRun.Id.Value);
        AddUuid(insert, "source_parse_run_id", write.SourceParseRunId.Value);
        AddUuid(insert, "representation_fetch_id", write.RepresentationFetchId.Value);
        AddText(insert, "capability_kind", ToKind(write.Kind));
        AddText(insert, "source_map_name", write.SourceMapName);
        AddNullableInteger(insert, "source_region_id", write.SourceRegionId);
        AddNullableInteger(
            insert,
            "source_scorched_victory_towns",
            write.SourceScorchedVictoryTowns);
        AddNullableBigint(insert, "source_version", write.SourceVersion);
        AddNullableBigint(
            insert,
            "source_last_updated_ms",
            write.SourceLastUpdatedMs);
        AddNullableTimestamp(
            insert,
            "source_updated_at",
            write.SourceUpdatedAt);
        AddBoolean(
            insert,
            "source_map_items_array_present",
            write.SourceMapItemsArrayPresent);
        AddBoolean(
            insert,
            "source_map_text_items_array_present",
            write.SourceMapTextItemsArrayPresent);
        AddInteger(insert, "item_count", write.Items.Count);
        AddInteger(insert, "text_item_count", write.TextItems.Count);

        var created = await ReadSnapshotAsync(insert, cancellationToken);
        if (created is not null)
        {
            return created;
        }

        var existing = await GetSnapshotByNormalizationRunAsync(
            connection,
            transaction,
            normalizationRun.Id,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "Map-snapshot uniqueness conflict was observed but no row was readable.");

        EnsureSnapshotEquivalent(
            existing,
            normalizationRun,
            write);

        return existing;
    }

    private static async Task InsertItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MapSnapshotId snapshotId,
        IReadOnlyList<MapItemOccurrenceCandidate> items,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO evidence.map_item_occurrences
                    (id, map_snapshot_id, source_ordinal,
                     raw_team_id, raw_icon_type, x, y,
                     raw_flags, raw_view_direction)
                VALUES
                    (@id, @map_snapshot_id, @source_ordinal,
                     @raw_team_id, @raw_icon_type, @x, @y,
                     @raw_flags, @raw_view_direction);
                """;

            AddUuid(command, "id", MapItemOccurrenceId.New().Value);
            AddUuid(command, "map_snapshot_id", snapshotId.Value);
            AddInteger(command, "source_ordinal", item.SourceOrdinal);
            AddNullableText(command, "raw_team_id", item.RawTeamId);
            AddNullableInteger(command, "raw_icon_type", item.RawIconType);
            AddNullableDouble(command, "x", item.X);
            AddNullableDouble(command, "y", item.Y);
            AddNullableInteger(command, "raw_flags", item.RawFlags);
            AddNullableInteger(
                command,
                "raw_view_direction",
                item.RawViewDirection);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertTextItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MapSnapshotId snapshotId,
        IReadOnlyList<MapTextOccurrenceCandidate> items,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO evidence.map_text_occurrences
                    (id, map_snapshot_id, source_ordinal,
                     text, x, y, raw_map_marker_type)
                VALUES
                    (@id, @map_snapshot_id, @source_ordinal,
                     @text, @x, @y, @raw_map_marker_type);
                """;

            AddUuid(command, "id", MapTextOccurrenceId.New().Value);
            AddUuid(command, "map_snapshot_id", snapshotId.Value);
            AddInteger(command, "source_ordinal", item.SourceOrdinal);
            AddNullableText(command, "text", item.Text);
            AddNullableDouble(command, "x", item.X);
            AddNullableDouble(command, "y", item.Y);
            AddNullableText(
                command,
                "raw_map_marker_type",
                item.RawMapMarkerType);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<NormalizationRunDescriptor?>
        GetNormalizationRunByIdAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            NormalizationRunId id,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {NormalizationColumns}
            FROM evidence.normalization_runs
            WHERE id = @id;
            """;
        AddUuid(command, "id", id.Value);
        return await ReadNormalizationRunAsync(command, cancellationToken);
    }

    private static async Task<NormalizationRunDescriptor?>
        GetNormalizationRunAsync(
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
        return await ReadNormalizationRunAsync(command, cancellationToken);
    }

    private static async Task<NormalizationRunDescriptor?>
        ReadNormalizationRunAsync(
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
            reader.GetString(3) switch
            {
                "normalized" => NormalizationRunOutcome.Normalized,
                "rejected" => NormalizationRunOutcome.Rejected,
                "failed" => NormalizationRunOutcome.Failed,
                var unknown => throw new CanonicalStateIntegrityException(
                    $"Unknown durable normalization outcome '{unknown}'."),
            },
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));
    }

    private static async Task<MapSnapshotDescriptor?>
        GetSnapshotByNormalizationRunAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            NormalizationRunId normalizationRunId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {SnapshotColumns}
            FROM evidence.map_snapshots
            WHERE normalization_run_id = @normalization_run_id;
            """;
        AddUuid(
            command,
            "normalization_run_id",
            normalizationRunId.Value);
        return await ReadSnapshotAsync(command, cancellationToken);
    }

    private static async Task<MapSnapshotDescriptor?> ReadSnapshotAsync(
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

        return new MapSnapshotDescriptor(
            new MapSnapshotId(reader.GetGuid(0)),
            new NormalizationRunId(reader.GetGuid(1)),
            new SourceParseRunId(reader.GetGuid(2)),
            new FetchId(reader.GetGuid(3)),
            ParseKind(reader.GetString(4)),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.IsDBNull(10)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(10),
            reader.GetBoolean(11),
            reader.GetBoolean(12),
            reader.GetInt32(13),
            reader.GetInt32(14),
            reader.GetFieldValue<DateTimeOffset>(15));
    }

    private static async Task<IReadOnlyList<MapItemOccurrenceDescriptor>>
        GetItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            MapSnapshotId snapshotId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                id, map_snapshot_id, source_ordinal,
                raw_team_id, raw_icon_type, x, y,
                raw_flags, raw_view_direction
            FROM evidence.map_item_occurrences
            WHERE map_snapshot_id = @map_snapshot_id
            ORDER BY source_ordinal;
            """;
        AddUuid(command, "map_snapshot_id", snapshotId.Value);

        var result = new List<MapItemOccurrenceDescriptor>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new MapItemOccurrenceDescriptor(
                    new MapItemOccurrenceId(reader.GetGuid(0)),
                    new MapSnapshotId(reader.GetGuid(1)),
                    reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<MapTextOccurrenceDescriptor>>
        GetTextItemsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            MapSnapshotId snapshotId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                id, map_snapshot_id, source_ordinal,
                text, x, y, raw_map_marker_type
            FROM evidence.map_text_occurrences
            WHERE map_snapshot_id = @map_snapshot_id
            ORDER BY source_ordinal;
            """;
        AddUuid(command, "map_snapshot_id", snapshotId.Value);

        var result = new List<MapTextOccurrenceDescriptor>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new MapTextOccurrenceDescriptor(
                    new MapTextOccurrenceId(reader.GetGuid(0)),
                    new MapSnapshotId(reader.GetGuid(1)),
                    reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return result;
    }

    private static void EnsureSnapshotEquivalent(
        MapSnapshotDescriptor existing,
        NormalizationRunDescriptor normalizationRun,
        MapSnapshotWrite write)
    {
        if (existing.NormalizationRunId != normalizationRun.Id ||
            existing.SourceParseRunId != write.SourceParseRunId ||
            existing.RepresentationFetchId != write.RepresentationFetchId ||
            existing.Kind != write.Kind ||
            !string.Equals(
                existing.SourceMapName,
                write.SourceMapName,
                StringComparison.Ordinal) ||
            existing.SourceRegionId != write.SourceRegionId ||
            existing.SourceScorchedVictoryTowns !=
                write.SourceScorchedVictoryTowns ||
            existing.SourceVersion != write.SourceVersion ||
            existing.SourceLastUpdatedMs != write.SourceLastUpdatedMs ||
            existing.SourceUpdatedAt != write.SourceUpdatedAt ||
            existing.SourceMapItemsArrayPresent !=
                write.SourceMapItemsArrayPresent ||
            existing.SourceMapTextItemsArrayPresent !=
                write.SourceMapTextItemsArrayPresent ||
            existing.ItemCount != write.Items.Count ||
            existing.TextItemCount != write.TextItems.Count)
        {
            throw new CanonicalStateIntegrityException(
                "Repeated map snapshot write differs from durable snapshot state.");
        }
    }

    private static void EnsureOccurrencesEquivalent(
        MapSnapshotDescriptor snapshot,
        IReadOnlyList<MapItemOccurrenceDescriptor> items,
        IReadOnlyList<MapTextOccurrenceDescriptor> textItems,
        MapSnapshotWrite write)
    {
        if (items.Count != snapshot.ItemCount ||
            textItems.Count != snapshot.TextItemCount ||
            items.Count != write.Items.Count ||
            textItems.Count != write.TextItems.Count)
        {
            throw new CanonicalStateIntegrityException(
                "Map snapshot child occurrence counts do not match durable snapshot metadata.");
        }

        for (var index = 0; index < items.Count; index++)
        {
            var durable = items[index];
            var supplied = write.Items[index];

            if (durable.SourceOrdinal != supplied.SourceOrdinal ||
                !string.Equals(
                    durable.RawTeamId,
                    supplied.RawTeamId,
                    StringComparison.Ordinal) ||
                durable.RawIconType != supplied.RawIconType ||
                !Nullable.Equals(durable.X, supplied.X) ||
                !Nullable.Equals(durable.Y, supplied.Y) ||
                durable.RawFlags != supplied.RawFlags ||
                durable.RawViewDirection != supplied.RawViewDirection)
            {
                throw new CanonicalStateIntegrityException(
                    $"Map item occurrence {index} differs from durable state.");
            }
        }

        for (var index = 0; index < textItems.Count; index++)
        {
            var durable = textItems[index];
            var supplied = write.TextItems[index];

            if (durable.SourceOrdinal != supplied.SourceOrdinal ||
                !string.Equals(
                    durable.Text,
                    supplied.Text,
                    StringComparison.Ordinal) ||
                !Nullable.Equals(durable.X, supplied.X) ||
                !Nullable.Equals(durable.Y, supplied.Y) ||
                !string.Equals(
                    durable.RawMapMarkerType,
                    supplied.RawMapMarkerType,
                    StringComparison.Ordinal))
            {
                throw new CanonicalStateIntegrityException(
                    $"Map text occurrence {index} differs from durable state.");
            }
        }
    }

    private static string ToKind(MapSnapshotKind kind) =>
        kind switch
        {
            MapSnapshotKind.Static => "static",
            MapSnapshotKind.Dynamic => "dynamic",
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown map snapshot kind."),
        };

    private static MapSnapshotKind ParseKind(string value) =>
        value switch
        {
            "static" => MapSnapshotKind.Static,
            "dynamic" => MapSnapshotKind.Dynamic,
            _ => throw new CanonicalStateIntegrityException(
                $"Unknown durable map snapshot kind '{value}'."),
        };

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
            (object?)value ?? DBNull.Value;

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
            (object?)value ?? DBNull.Value;

    private static void AddNullableBigint(
        NpgsqlCommand command,
        string name,
        long? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Bigint).Value =
            (object?)value ?? DBNull.Value;

    private static void AddNullableDouble(
        NpgsqlCommand command,
        string name,
        double? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Double).Value =
            (object?)value ?? DBNull.Value;

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
            (object?)value ?? DBNull.Value;

    private static void AddBoolean(
        NpgsqlCommand command,
        string name,
        bool value) =>
        command.Parameters.Add(name, NpgsqlDbType.Boolean).Value = value;
}
