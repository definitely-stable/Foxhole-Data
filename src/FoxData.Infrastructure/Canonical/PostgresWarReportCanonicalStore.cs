using System.Data;
using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Core.Runtime;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Canonical;

public sealed class PostgresWarReportCanonicalStore(NpgsqlDataSource dataSource)
    : IWarReportCanonicalStore
{
    private const string NormalizationColumns =
        """
        id, source_parse_run_id, normalizer_version, outcome, error_code,
        started_at, completed_at, created_at
        """;

    public async Task<WarReportCanonicalResult?> GetByNormalizationRunAsync(
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

        var observation = await GetObservationByNormalizationRunAsync(
            connection,
            transaction: null,
            normalizationRunId,
            cancellationToken);

        if (observation is null)
        {
            return null;
        }

        var warRegion = await GetWarRegionByIdAsync(
            connection,
            transaction: null,
            observation.WarRegionId,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"War-report observation {observation.Id} references missing war-region {observation.WarRegionId}.");

        return new WarReportCanonicalResult(
            warRegion,
            observation,
            normalizationRun);
    }

    public async Task<WarReportCanonicalResult> RecordAcceptedAsync(
        WarReportCanonicalWrite write,
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

        var warRegion = await EnsureWarRegionContextAsync(
            connection,
            transaction,
            write,
            cancellationToken);

        await EnsureWarContextAsync(
            connection,
            transaction,
            write,
            cancellationToken);

        var normalizationRun = await RecordNormalizationRunAsync(
            connection,
            transaction,
            write,
            cancellationToken);

        var observation = await RecordObservationAsync(
            connection,
            transaction,
            warRegion,
            normalizationRun,
            write,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new WarReportCanonicalResult(
            warRegion,
            observation,
            normalizationRun);
    }

    private static async Task EnsureParseProvenanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarReportCanonicalWrite write,
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
                endpoint.capability_key,
                endpoint.semantic_key
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
        var semanticKey = reader.GetString(5);
        var expectedSemanticKey = $"war-report/{write.SourceMapName}";

        if (representationFetchId != write.RepresentationFetchId ||
            shardId != write.ShardId ||
            !string.Equals(
                parseCapabilityKey,
                write.CapabilityKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                endpointCapabilityKey,
                write.CapabilityKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                semanticKey,
                expectedSemanticKey,
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "War-report canonical write does not match its durable source-parse provenance.");
        }

        if (parseOutcome is not ("parsed" or "parsed_with_unknowns"))
        {
            throw new CanonicalStateIntegrityException(
                $"Source parse run {write.SourceParseRunId} is not a successful parse.");
        }
    }

    private static async Task<WarRegionDescriptor> EnsureWarRegionContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarReportCanonicalWrite write,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                membership.id,
                membership.war_id,
                membership.region_id,
                membership.source_map_name,
                membership.source_region_id,
                membership.first_seen_at,
                membership.last_seen_at,
                membership.created_at,
                war.shard_id
            FROM runtime.war_regions AS membership
            INNER JOIN runtime.wars AS war
                ON war.id = membership.war_id
            WHERE membership.id = @war_region_id
            FOR SHARE;
            """;

        AddUuid(command, "war_region_id", write.WarRegionId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CanonicalStateIntegrityException(
                $"War-region {write.WarRegionId} does not exist.");
        }

        var membership = new WarRegionDescriptor(
            new WarRegionId(reader.GetGuid(0)),
            new WarId(reader.GetGuid(1)),
            new RegionId(reader.GetGuid(2)),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));
        var shardId = new ShardId(reader.GetGuid(8));

        if (membership.WarId != write.WarId ||
            shardId != write.ShardId ||
            !string.Equals(
                membership.SourceMapName,
                write.SourceMapName,
                StringComparison.Ordinal) ||
            membership.FirstSeenAt > write.ObservedAt)
        {
            throw new CanonicalStateIntegrityException(
                "War-report canonical write does not match its exact war-region context.");
        }

        return membership;
    }

    private static async Task EnsureWarContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarReportCanonicalWrite write,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT observation.war_id
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

        var value = await command.ExecuteScalarAsync(cancellationToken);

        if (value is not Guid warId)
        {
            throw new CanonicalStateIntegrityException(
                "War-report canonical write has no qualifying durable war context.");
        }

        if (new WarId(warId) != write.WarId)
        {
            throw new CanonicalStateIntegrityException(
                "War-report canonical write does not target the latest durable war context at its observation boundary.");
        }
    }

    private static async Task<NormalizationRunDescriptor> RecordNormalizationRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarReportCanonicalWrite write,
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
                "Repeated accepted war-report normalization conflicts with the durable normalization outcome.");
        }

        return existing;
    }

    private static async Task<WarReportObservationDescriptor> RecordObservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarRegionDescriptor warRegion,
        NormalizationRunDescriptor normalizationRun,
        WarReportCanonicalWrite write,
        CancellationToken cancellationToken)
    {
        var proposedId = WarReportObservationId.New();

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO runtime.war_report_observations
                (id, war_region_id, normalization_run_id,
                 representation_fetch_id, observed_at,
                 total_enlistments, colonial_casualties,
                 warden_casualties, day_of_war)
            VALUES
                (@id, @war_region_id, @normalization_run_id,
                 @representation_fetch_id, @observed_at,
                 @total_enlistments, @colonial_casualties,
                 @warden_casualties, @day_of_war)
            ON CONFLICT (normalization_run_id)
            DO NOTHING
            RETURNING
                id, war_region_id, normalization_run_id,
                representation_fetch_id, observed_at, recorded_at,
                total_enlistments, colonial_casualties,
                warden_casualties, day_of_war;
            """;

        AddUuid(insert, "id", proposedId.Value);
        AddUuid(insert, "war_region_id", warRegion.Id.Value);
        AddUuid(insert, "normalization_run_id", normalizationRun.Id.Value);
        AddUuid(insert, "representation_fetch_id", write.RepresentationFetchId.Value);
        AddTimestamp(insert, "observed_at", write.ObservedAt);
        AddNullableBigint(insert, "total_enlistments", write.Snapshot.TotalEnlistments);
        AddNullableBigint(insert, "colonial_casualties", write.Snapshot.ColonialCasualties);
        AddNullableBigint(insert, "warden_casualties", write.Snapshot.WardenCasualties);
        AddNullableInteger(insert, "day_of_war", write.Snapshot.DayOfWar);

        var created = await ReadObservationAsync(
            insert,
            cancellationToken);

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
                "War-report observation uniqueness conflict was observed but the existing row was not readable.");

        EnsureEquivalent(
            existing,
            warRegion,
            normalizationRun,
            write);

        return existing;
    }

    private static void EnsureEquivalent(
        WarReportObservationDescriptor existing,
        WarRegionDescriptor warRegion,
        NormalizationRunDescriptor normalizationRun,
        WarReportCanonicalWrite supplied)
    {
        var snapshot = supplied.Snapshot;

        if (existing.WarRegionId != warRegion.Id ||
            existing.NormalizationRunId != normalizationRun.Id ||
            existing.RepresentationFetchId != supplied.RepresentationFetchId ||
            existing.ObservedAt != supplied.ObservedAt ||
            existing.TotalEnlistments != snapshot.TotalEnlistments ||
            existing.ColonialCasualties != snapshot.ColonialCasualties ||
            existing.WardenCasualties != snapshot.WardenCasualties ||
            existing.DayOfWar != snapshot.DayOfWar)
        {
            throw new CanonicalStateIntegrityException(
                "Repeated war-report canonical write differs from the durable observation.");
        }
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

    private static async Task<NormalizationRunDescriptor?> GetNormalizationRunByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        NormalizationRunId normalizationRunId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {NormalizationColumns}
            FROM evidence.normalization_runs
            WHERE id = @normalization_run_id;
            """;

        AddUuid(command, "normalization_run_id", normalizationRunId.Value);

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

    private static async Task<WarReportObservationDescriptor?>
        GetObservationByNormalizationRunAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            NormalizationRunId normalizationRunId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                id, war_region_id, normalization_run_id,
                representation_fetch_id, observed_at, recorded_at,
                total_enlistments, colonial_casualties,
                warden_casualties, day_of_war
            FROM runtime.war_report_observations
            WHERE normalization_run_id = @normalization_run_id;
            """;

        AddUuid(command, "normalization_run_id", normalizationRunId.Value);

        return await ReadObservationAsync(
            command,
            cancellationToken);
    }

    private static async Task<WarReportObservationDescriptor?> ReadObservationAsync(
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

        return new WarReportObservationDescriptor(
            new WarReportObservationId(reader.GetGuid(0)),
            new WarRegionId(reader.GetGuid(1)),
            new NormalizationRunId(reader.GetGuid(2)),
            new FetchId(reader.GetGuid(3)),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetInt32(9));
    }

    private static async Task<WarRegionDescriptor?> GetWarRegionByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        WarRegionId warRegionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                id, war_id, region_id, source_map_name, source_region_id,
                first_seen_at, last_seen_at, created_at
            FROM runtime.war_regions
            WHERE id = @war_region_id;
            """;

        AddUuid(command, "war_region_id", warRegionId.Value);

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

    private static void AddTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value = value;

    private static void AddNullableBigint(
        NpgsqlCommand command,
        string name,
        long? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Bigint).Value =
            value is null ? DBNull.Value : value.Value;

    private static void AddNullableInteger(
        NpgsqlCommand command,
        string name,
        int? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Integer).Value =
            value is null ? DBNull.Value : value.Value;
}
