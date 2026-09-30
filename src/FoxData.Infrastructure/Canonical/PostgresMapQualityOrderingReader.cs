using System.Data;
using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Core.Quality;
using FoxData.Core.Runtime;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Canonical;

// Both the preliminary Worker reader and the locked write transaction use
// this one ordering implementation. Never duplicate its ORDER BY rules.
public sealed class PostgresMapQualityOrderingReader(NpgsqlDataSource dataSource)
    : IMapQualityOrderingReader
{
    public async Task<MapQualityOrderingPlan> GetPlanAsync(
        MapSnapshotId mapSnapshotId,
        WarRegionId warRegionId,
        FetchId validationFetchId,
        string taxonomyVersion,
        string qualityPolicyVersion,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        return await MapQualityOrderingQueries.GetPlanAsync(
            connection,
            null,
            mapSnapshotId,
            warRegionId,
            validationFetchId,
            taxonomyVersion,
            qualityPolicyVersion,
            cancellationToken);
    }
}

internal static class MapQualityOrderingQueries
{
    public static async Task<MapQualityOrderingPlan> GetPlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        MapSnapshotId snapshotId,
        WarRegionId regionId,
        FetchId validationId,
        string taxonomyVersion,
        string policyVersion,
        CancellationToken cancellationToken)
    {
        await using var candidate = connection.CreateCommand();
        candidate.Transaction = transaction;
        candidate.CommandText =
            """
            SELECT
                validation.retrieved_at,
                source_endpoint.id,
                region.first_seen_at,
                source_parse.parser_version,
                normalization.normalizer_version,
                source_parse.structural_fingerprint
            FROM evidence.map_snapshots AS snapshot
            JOIN evidence.source_parse_runs AS source_parse
                ON source_parse.id = snapshot.source_parse_run_id
            JOIN evidence.normalization_runs AS normalization
                ON normalization.id = snapshot.normalization_run_id
            JOIN evidence.fetches AS representation
                ON representation.id = snapshot.representation_fetch_id
            JOIN ingest.attempts AS representation_attempt
                ON representation_attempt.id = representation.attempt_id
            JOIN sources.endpoints AS source_endpoint
                ON source_endpoint.id = representation.endpoint_id
            JOIN evidence.fetches AS validation
                ON validation.id = @validation_id
            JOIN ingest.attempts AS validation_attempt
                ON validation_attempt.id = validation.attempt_id
            JOIN runtime.war_regions AS region
                ON region.id = @region_id
            JOIN runtime.wars AS war
                ON war.id = region.war_id
            WHERE snapshot.id = @snapshot_id
              AND snapshot.source_map_name = region.source_map_name
              AND source_endpoint.shard_id = war.shard_id
              AND source_endpoint.id = validation.endpoint_id
              AND representation_attempt.outcome_code = 'captured_current'
              AND validation_attempt.outcome_code = 'captured_current'
              AND source_parse.representation_fetch_id = representation.id
              AND normalization.source_parse_run_id = source_parse.id
              AND normalization.outcome = 'normalized'
              AND validation.retrieved_at >= region.first_seen_at
              AND (
                    (validation.status_code = 200
                      AND validation.id = representation.id
                      AND validation.payload_id IS NOT NULL)
                    OR
                    (validation.status_code = 304
                      AND validation.payload_id IS NULL
                      AND validation.prior_fetch_id = representation.id)
                  );
            """;
        Uuid(candidate, "validation_id", validationId.Value);
        Uuid(candidate, "region_id", regionId.Value);
        Uuid(candidate, "snapshot_id", snapshotId.Value);

        DateTimeOffset observedAt;
        DateTimeOffset firstSeenAt;
        Guid endpointId;
        string parserVersion;
        string normalizerVersion;
        string? currentFingerprint;
        await using (var reader = await candidate.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new CanonicalStateIntegrityException(
                    "Map quality ordering requires proven snapshot, shard, WarRegion and validation Fetch lineage.");
            }

            observedAt = reader.GetFieldValue<DateTimeOffset>(0);
            endpointId = reader.GetGuid(1);
            firstSeenAt = reader.GetFieldValue<DateTimeOffset>(2);
            parserVersion = reader.GetString(3);
            normalizerVersion = reader.GetString(4);
            currentFingerprint = reader.IsDBNull(5)
                ? null
                : reader.GetString(5);
        }

        // Starting with authoritative Fetches prevents a crash before parse
        // or normalization from making an older representation invisible.
        await using var blockerCommand = connection.CreateCommand();
        blockerCommand.Transaction = transaction;
        blockerCommand.CommandText =
            """
            SELECT
                CASE
                    WHEN parsed.id IS NULL THEN 'earlier_parse_missing'
                    WHEN normalized.id IS NULL THEN 'earlier_normalization_missing'
                    WHEN prior_snapshot.id IS NULL THEN 'earlier_snapshot_missing'
                    ELSE 'earlier_quality_missing'
                END
            FROM evidence.fetches AS previous
            JOIN ingest.attempts AS attempt
                ON attempt.id = previous.attempt_id
            LEFT JOIN evidence.source_parse_runs AS parsed
                ON parsed.representation_fetch_id = previous.id
               AND parsed.capability_key = @capability_key
               AND parsed.parser_version = @parser_version
            LEFT JOIN evidence.normalization_runs AS normalized
                ON normalized.source_parse_run_id = parsed.id
               AND normalized.normalizer_version = @normalizer_version
            LEFT JOIN evidence.map_snapshots AS prior_snapshot
                ON prior_snapshot.normalization_run_id = normalized.id
            LEFT JOIN quality.map_quality_runs AS quality
                ON quality.map_snapshot_id = prior_snapshot.id
               AND quality.war_region_id = @region_id
               AND quality.validation_fetch_id = previous.id
               AND quality.taxonomy_version = @taxonomy_version
               AND quality.quality_policy_version = @policy_version
            WHERE previous.endpoint_id = @endpoint_id
              AND previous.status_code = 200
              AND previous.payload_id IS NOT NULL
              AND attempt.outcome_code = 'captured_current'
              AND previous.retrieved_at >= @first_seen_at
              AND (previous.retrieved_at, previous.id)
                    < (@observed_at, @validation_id)
              AND (
                    parsed.id IS NULL
                    OR (
                        parsed.outcome IN ('parsed', 'parsed_with_unknowns')
                        AND (
                            normalized.id IS NULL
                            OR (
                                normalized.outcome = 'normalized'
                                AND (
                                    prior_snapshot.id IS NULL
                                    OR quality.id IS NULL
                                )
                            )
                        )
                    )
                  )
            ORDER BY previous.retrieved_at, previous.id
            LIMIT 1;
            """;
        Uuid(blockerCommand, "endpoint_id", endpointId);
        Uuid(blockerCommand, "region_id", regionId.Value);
        Uuid(blockerCommand, "validation_id", validationId.Value);
        Timestamp(blockerCommand, "first_seen_at", firstSeenAt);
        Timestamp(blockerCommand, "observed_at", observedAt);
        String(blockerCommand, "capability_key",
            (await GetCapabilityAsync(connection, transaction, snapshotId,
                cancellationToken)));
        String(blockerCommand, "parser_version", parserVersion);
        String(blockerCommand, "normalizer_version", normalizerVersion);
        String(blockerCommand, "taxonomy_version", taxonomyVersion);
        String(blockerCommand, "policy_version", policyVersion);

        var blocker = (string?)await blockerCommand.ExecuteScalarAsync(
            cancellationToken);
        if (blocker is not null)
        {
            return MapQualityOrderingPlan.Deferred(observedAt, blocker);
        }

        await using var baseline = connection.CreateCommand();
        baseline.Transaction = transaction;
        baseline.CommandText =
            """
            SELECT
                observation.id,
                observation.war_region_id,
                observation.map_snapshot_id,
                observation.quality_run_id,
                observation.validation_fetch_id,
                observation.capability_kind,
                observation.observed_at,
                observation.source_updated_at,
                observation.recorded_at,
                source_parse.structural_fingerprint
            FROM runtime.map_observations AS observation
            JOIN quality.map_quality_runs AS quality
                ON quality.id = observation.quality_run_id
            JOIN evidence.map_snapshots AS previous_snapshot
                ON previous_snapshot.id = observation.map_snapshot_id
            JOIN evidence.source_parse_runs AS source_parse
                ON source_parse.id = previous_snapshot.source_parse_run_id
            JOIN evidence.map_snapshots AS target
                ON target.id = @snapshot_id
            WHERE observation.war_region_id = @region_id
              AND observation.capability_kind = target.capability_kind
              AND quality.taxonomy_version = @taxonomy_version
              AND quality.quality_policy_version = @policy_version
              AND quality.decision = 'accepted'
              AND (observation.observed_at, observation.validation_fetch_id)
                    < (@observed_at, @validation_id)
            ORDER BY observation.observed_at DESC,
                     observation.validation_fetch_id DESC
            LIMIT 1;
            """;
        Uuid(baseline, "snapshot_id", snapshotId.Value);
        Uuid(baseline, "region_id", regionId.Value);
        Uuid(baseline, "validation_id", validationId.Value);
        Timestamp(baseline, "observed_at", observedAt);
        String(baseline, "taxonomy_version", taxonomyVersion);
        String(baseline, "policy_version", policyVersion);

        await using var row = await baseline.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        if (!await row.ReadAsync(cancellationToken))
        {
            return MapQualityOrderingPlan.Ready(
                observedAt,
                null,
                currentFingerprint,
                null);
        }

        var prior = new MapObservationDescriptor(
            new MapObservationId(row.GetGuid(0)),
            new WarRegionId(row.GetGuid(1)),
            new MapSnapshotId(row.GetGuid(2)),
            new MapQualityRunId(row.GetGuid(3)),
            new FetchId(row.GetGuid(4)),
            row.GetString(5) switch
            {
                "static" => MapSnapshotKind.Static,
                "dynamic" => MapSnapshotKind.Dynamic,
                var value => throw new CanonicalStateIntegrityException(
                    $"Unknown map observation kind '{value}'."),
            },
            row.GetFieldValue<DateTimeOffset>(6),
            row.IsDBNull(7) ? null : row.GetFieldValue<DateTimeOffset>(7),
            row.GetFieldValue<DateTimeOffset>(8));

        return MapQualityOrderingPlan.Ready(
            observedAt,
            prior,
            currentFingerprint,
            row.IsDBNull(9) ? null : row.GetString(9));
    }

    private static async Task<string> GetCapabilityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        MapSnapshotId id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT CASE capability_kind
                WHEN 'static' THEN 'static-map-state'
                WHEN 'dynamic' THEN 'dynamic-map-state'
                ELSE NULL END
            FROM evidence.map_snapshots WHERE id = @id;
            """;
        Uuid(command, "id", id.Value);
        return (string?)await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "Unknown snapshot capability for quality ordering.");
    }

    private static void Uuid(NpgsqlCommand command, string name, Guid value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = value;

    private static void Timestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value = value;

    private static void String(
        NpgsqlCommand command,
        string name,
        string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value;
}
