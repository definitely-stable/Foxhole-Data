using System.Data;
using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Core.Runtime;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Canonical;

public sealed class PostgresRegionCanonicalStore(NpgsqlDataSource dataSource)
    : IRegionCanonicalStore
{
    private const string NormalizationColumns =
        """
        id, source_parse_run_id, normalizer_version, outcome, error_code,
        started_at, completed_at, created_at
        """;

    public async Task<RegionCanonicalResult> RecordAcceptedAsync(
        RegionCanonicalWrite write,
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

        var warContext = await EnsureWarContextAsync(
            connection,
            transaction,
            write,
            cancellationToken);

        var normalizationRun = await RecordNormalizationRunAsync(
            connection,
            transaction,
            write,
            cancellationToken);

        var memberships = new List<RegionMembershipDescriptor>(
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

            memberships.Add(
                new RegionMembershipDescriptor(
                    region,
                    membership));
        }

        await transaction.CommitAsync(cancellationToken);

        return new RegionCanonicalResult(
            normalizationRun,
            warContext,
            memberships);
    }

    private static async Task EnsureParseProvenanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RegionCanonicalWrite write,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                parse_run.representation_fetch_id,
                parse_run.outcome,
                shard.id,
                parse_run.capability_key,
                endpoint.capability_key
            FROM evidence.source_parse_runs AS parse_run
            INNER JOIN evidence.fetches AS representation_fetch
                ON representation_fetch.id = parse_run.representation_fetch_id
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = representation_fetch.endpoint_id
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
        var parseCapabilityKey = reader.GetString(3);
        var endpointCapabilityKey = reader.GetString(4);

        if (representationFetchId != write.RepresentationFetchId ||
            shardId != write.ShardId ||
            !string.Equals(
                parseCapabilityKey,
                write.CapabilityKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                endpointCapabilityKey,
                write.CapabilityKey,
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Region canonical write does not match its durable source-parse provenance.");
        }

        if (parseOutcome is not ("parsed" or "parsed_with_unknowns"))
        {
            throw new CanonicalStateIntegrityException(
                $"Source parse run {write.SourceParseRunId} is not a successful parse.");
        }
    }

    private static async Task<WarContextDescriptor> EnsureWarContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RegionCanonicalWrite write,
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

        AddUuid(command, "shard_id", write.ShardId.Value);
        AddTimestamp(command, "observed_at", write.ObservedAt);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CanonicalStateIntegrityException(
                "Region canonical write has no qualifying durable war context.");
        }

        var context = new WarContextDescriptor(
            new WarId(reader.GetGuid(0)),
            new ShardId(reader.GetGuid(1)),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3));

        if (context.WarId != write.WarId)
        {
            throw new CanonicalStateIntegrityException(
                "Region canonical write does not target the latest durable war context at its observation boundary.");
        }

        return context;
    }

    private static async Task<NormalizationRunDescriptor> RecordNormalizationRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RegionCanonicalWrite write,
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

        throw new CanonicalStateIntegrityException(
            existing.Outcome == NormalizationRunOutcome.Normalized &&
            existing.ErrorCode is null
                ? "Accepted region normalization already exists; replay must not mutate membership projections again."
                : "Repeated accepted region normalization conflicts with the durable normalization outcome.");
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
            SELECT
                id, canonical_key, display_name, created_at
            FROM runtime.regions
            WHERE canonical_key = @canonical_key
            FOR UPDATE;
            """;
        AddText(select, "canonical_key", candidate.CanonicalKey);

        var existing = await ReadRegionAsync(
            select,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "Region uniqueness conflict was observed but the existing row was not readable.");

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
        RegionCanonicalWrite write,
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
                    "War-region uniqueness conflict was observed but the existing row was not readable.");
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
            existing.SourceRegionId ??
            candidate.SourceRegionId;
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
                "War-region row disappeared while updating discovery bounds.");
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
}
