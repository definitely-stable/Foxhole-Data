using System.Data;
using System.Text;
using System.Text.Json;
using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Core.Runtime;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Canonical;

public sealed class PostgresMapQualityStore(NpgsqlDataSource dataSource)
    : IMapQualityStore
{
    public async Task<MapQualityResult?> GetAsync(
        MapSnapshotId mapSnapshotId,
        WarRegionId warRegionId,
        FetchId validationFetchId,
        string taxonomyVersion,
        string qualityPolicyVersion,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);

        var run = await GetRunByIdentityAsync(
            connection,
            transaction: null,
            mapSnapshotId,
            warRegionId,
            validationFetchId,
            taxonomyVersion,
            qualityPolicyVersion,
            cancellationToken);

        if (run is null)
        {
            return null;
        }

        return await BuildResultAsync(
            connection,
            transaction: null,
            run,
            cancellationToken);
    }

    public async Task<MapQualityResult> RecordAsync(
        MapQualityWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        var snapshot = await GetSnapshotAsync(
            connection,
            transaction,
            write.MapSnapshotId,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"Map snapshot {write.MapSnapshotId} does not exist.");

        var warRegionContext = await LockWarRegionAsync(
            connection,
            transaction,
            write.WarRegionId,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"War-region {write.WarRegionId} does not exist.");
        var warRegion = warRegionContext.WarRegion;

        var validation = await GetValidationContextAsync(
            connection,
            transaction,
            write.ValidationFetchId,
            snapshot.RepresentationFetchId,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"Validation Fetch {write.ValidationFetchId} does not exist.");

        EnsureBindingProvenance(
            snapshot,
            warRegionContext,
            validation);

        var expectedBaseline =
            await GetLatestAcceptedBaselineAsync(
                connection,
                transaction,
                write.WarRegionId,
                snapshot.Kind,
                validation.RetrievedAt,
                cancellationToken);

        if (expectedBaseline?.Id != write.BaselineMapObservationId)
        {
            throw new CanonicalStateIntegrityException(
                $"Supplied baseline {write.BaselineMapObservationId?.ToString() ?? "<null>"} does not match durable latest accepted baseline {expectedBaseline?.Id.ToString() ?? "<null>"}.");
        }

        await EnsureFindingOccurrenceBindingsAsync(
            connection,
            transaction,
            snapshot.Id,
            write.Findings,
            cancellationToken);

        var existing = await GetRunByIdentityAsync(
            connection,
            transaction,
            write.MapSnapshotId,
            write.WarRegionId,
            write.ValidationFetchId,
            write.TaxonomyVersion,
            write.QualityPolicyVersion,
            cancellationToken);

        if (existing is not null)
        {
            EnsureRunEquivalent(existing, write);

            var result = await BuildResultAsync(
                connection,
                transaction,
                existing,
                cancellationToken);

            EnsureFindingsEquivalent(
                result.Findings,
                write.Findings);
            EnsureDecisionObservationInvariant(result);

            await transaction.CommitAsync(cancellationToken);
            return result;
        }

        if (write.Decision == MapQualityDecision.Accepted)
        {
            EnsureAcceptedSourceRegionCompatibility(
                snapshot,
                warRegion);
        }

        var run = await InsertRunAsync(
            connection,
            transaction,
            write,
            cancellationToken);

        await InsertFindingsAsync(
            connection,
            transaction,
            run.Id,
            write.Findings,
            cancellationToken);

        MapObservationDescriptor? observation = null;
        if (write.Decision == MapQualityDecision.Accepted)
        {
            observation = await InsertObservationAsync(
                connection,
                transaction,
                run,
                snapshot,
                validation,
                cancellationToken);

            warRegion = await EnrichSourceRegionIdAsync(
                connection,
                transaction,
                warRegion,
                snapshot.SourceRegionId,
                cancellationToken);
        }

        var findings = await GetFindingsAsync(
            connection,
            transaction,
            run.Id,
            cancellationToken);

        var resultCreated = new MapQualityResult(
            run,
            findings,
            observation,
            warRegion);

        EnsureFindingsEquivalent(findings, write.Findings);
        EnsureDecisionObservationInvariant(resultCreated);

        await transaction.CommitAsync(cancellationToken);
        return resultCreated;
    }

    private static async Task<MapSnapshotDescriptor?> GetSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MapSnapshotId snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                id, normalization_run_id, source_parse_run_id,
                representation_fetch_id, capability_kind, source_map_name,
                source_region_id, source_scorched_victory_towns,
                source_version, source_last_updated_ms, source_updated_at,
                source_map_items_array_present,
                source_map_text_items_array_present,
                item_count, text_item_count, recorded_at
            FROM evidence.map_snapshots
            WHERE id = @id;
            """;
        AddUuid(command, "id", snapshotId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadSnapshot(reader);
    }

    private static async Task<WarRegionLockContext?> LockWarRegionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarRegionId warRegionId,
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
            WHERE membership.id = @id
            FOR UPDATE OF membership;
            """;
        AddUuid(command, "id", warRegionId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WarRegionLockContext(
            ReadWarRegion(reader),
            new ShardId(reader.GetGuid(8)));
    }

    private static async Task<ValidationContext?> GetValidationContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FetchId validationFetchId,
        FetchId representationFetchId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                validation_fetch.id,
                validation_fetch.endpoint_id,
                validation_fetch.retrieved_at,
                validation_fetch.status_code,
                validation_fetch.payload_id,
                validation_fetch.prior_fetch_id,
                validation_attempt.outcome_code,
                representation_fetch.endpoint_id,
                representation_fetch.payload_id,
                endpoint.shard_id,
                endpoint.capability_key,
                endpoint.semantic_key
            FROM evidence.fetches AS validation_fetch
            INNER JOIN ingest.attempts AS validation_attempt
                ON validation_attempt.id = validation_fetch.attempt_id
            INNER JOIN evidence.fetches AS representation_fetch
                ON representation_fetch.id = @representation_fetch_id
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = validation_fetch.endpoint_id
            WHERE validation_fetch.id = @validation_fetch_id;
            """;

        AddUuid(
            command,
            "validation_fetch_id",
            validationFetchId.Value);
        AddUuid(
            command,
            "representation_fetch_id",
            representationFetchId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ValidationContext(
            new FetchId(reader.GetGuid(0)),
            new EndpointId(reader.GetGuid(1)),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4)
                ? null
                : new PayloadId(reader.GetGuid(4)),
            reader.IsDBNull(5)
                ? null
                : new FetchId(reader.GetGuid(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            new EndpointId(reader.GetGuid(7)),
            reader.IsDBNull(8)
                ? null
                : new PayloadId(reader.GetGuid(8)),
            new ShardId(reader.GetGuid(9)),
            reader.GetString(10),
            reader.GetString(11));
    }

    private static void EnsureBindingProvenance(
        MapSnapshotDescriptor snapshot,
        WarRegionLockContext warRegionContext,
        ValidationContext validation)
    {
        var warRegion = warRegionContext.WarRegion;

        var expectedCapability = snapshot.Kind switch
        {
            MapSnapshotKind.Static => "static-map-state",
            MapSnapshotKind.Dynamic => "dynamic-map-state",
            _ => throw new CanonicalStateIntegrityException(
                $"Unknown map snapshot kind '{snapshot.Kind}'."),
        };
        var expectedSemantic = snapshot.Kind switch
        {
            MapSnapshotKind.Static =>
                $"map-static/{snapshot.SourceMapName}",
            MapSnapshotKind.Dynamic =>
                $"map-dynamic/{snapshot.SourceMapName}",
            _ => throw new CanonicalStateIntegrityException(
                $"Unknown map snapshot kind '{snapshot.Kind}'."),
        };

        if (!string.Equals(
                validation.AttemptOutcomeCode,
                "captured_current",
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Map quality binding requires authoritative captured_current validation evidence.");
        }

        if (validation.EndpointId !=
                validation.RepresentationEndpointId ||
            validation.RepresentationPayloadId is null ||
            !string.Equals(
                validation.CapabilityKey,
                expectedCapability,
                StringComparison.Ordinal) ||
            !string.Equals(
                validation.SemanticKey,
                expectedSemantic,
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Map quality validation evidence does not match snapshot endpoint provenance.");
        }

        var bodyBearingValidation =
            validation.ValidationFetchId ==
                snapshot.RepresentationFetchId &&
            validation.StatusCode == 200 &&
            validation.ValidationPayloadId is not null;

        var notModifiedValidation =
            validation.StatusCode == 304 &&
            validation.ValidationPayloadId is null &&
            validation.PriorFetchId ==
                snapshot.RepresentationFetchId &&
            validation.ValidationFetchId !=
                snapshot.RepresentationFetchId;

        if (!bodyBearingValidation &&
            !notModifiedValidation)
        {
            throw new CanonicalStateIntegrityException(
                "Map quality validation Fetch is neither the body-bearing representation nor an exact 304 validation of it.");
        }

        if (validation.ShardId != warRegionContext.ShardId)
        {
            throw new CanonicalStateIntegrityException(
                "Map quality validation endpoint shard differs from the WarRegion war shard.");
        }

        if (!string.Equals(
                warRegion.SourceMapName,
                snapshot.SourceMapName,
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Map quality WarRegion source map identity differs from the normalized snapshot.");
        }

        if (warRegion.FirstSeenAt > validation.RetrievedAt)
        {
            throw new CanonicalStateIntegrityException(
                "Map quality validation predates the proven WarRegion membership boundary.");
        }
    }

    private static async Task<MapObservationDescriptor?>
        GetLatestAcceptedBaselineAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            WarRegionId warRegionId,
            MapSnapshotKind kind,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
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
                observation.recorded_at
            FROM runtime.map_observations AS observation
            INNER JOIN quality.map_quality_runs AS quality_run
                ON quality_run.id = observation.quality_run_id
            WHERE observation.war_region_id = @war_region_id
              AND observation.capability_kind = @capability_kind
              AND observation.observed_at < @observed_at
              AND quality_run.decision = 'accepted'
            ORDER BY
                observation.observed_at DESC,
                observation.validation_fetch_id DESC,
                observation.id DESC
            LIMIT 1;
            """;

        AddUuid(command, "war_region_id", warRegionId.Value);
        AddText(command, "capability_kind", ToStorage(kind));
        AddTimestamp(command, "observed_at", observedAt);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadObservation(reader);
    }

    private static async Task EnsureFindingOccurrenceBindingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MapSnapshotId snapshotId,
        IReadOnlyList<MapQualityFindingCandidate> findings,
        CancellationToken cancellationToken)
    {
        foreach (var finding in findings)
        {
            if (finding.MapItemOccurrenceId is { } itemId)
            {
                await EnsureOccurrenceSnapshotAsync(
                    connection,
                    transaction,
                    "evidence.map_item_occurrences",
                    itemId.Value,
                    snapshotId,
                    cancellationToken);
            }

            if (finding.MapTextOccurrenceId is { } textId)
            {
                await EnsureOccurrenceSnapshotAsync(
                    connection,
                    transaction,
                    "evidence.map_text_occurrences",
                    textId.Value,
                    snapshotId,
                    cancellationToken);
            }
        }
    }

    private static async Task EnsureOccurrenceSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        Guid occurrenceId,
        MapSnapshotId snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT map_snapshot_id FROM {table} WHERE id = @id;";
        AddUuid(command, "id", occurrenceId);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not Guid durableSnapshotId ||
            durableSnapshotId != snapshotId.Value)
        {
            throw new CanonicalStateIntegrityException(
                $"Quality finding occurrence {occurrenceId:D} does not belong to map snapshot {snapshotId}.");
        }
    }

    private static async Task<MapQualityRunDescriptor?> GetRunByIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        MapSnapshotId mapSnapshotId,
        WarRegionId warRegionId,
        FetchId validationFetchId,
        string taxonomyVersion,
        string qualityPolicyVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                id, map_snapshot_id, war_region_id,
                validation_fetch_id, taxonomy_version,
                quality_policy_version, baseline_map_observation_id,
                decision, started_at, completed_at, created_at
            FROM quality.map_quality_runs
            WHERE map_snapshot_id = @map_snapshot_id
              AND war_region_id = @war_region_id
              AND validation_fetch_id = @validation_fetch_id
              AND taxonomy_version = @taxonomy_version
              AND quality_policy_version = @quality_policy_version;
            """;

        AddUuid(command, "map_snapshot_id", mapSnapshotId.Value);
        AddUuid(command, "war_region_id", warRegionId.Value);
        AddUuid(
            command,
            "validation_fetch_id",
            validationFetchId.Value);
        AddText(command, "taxonomy_version", taxonomyVersion);
        AddText(
            command,
            "quality_policy_version",
            qualityPolicyVersion);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadRun(reader);
    }

    private static async Task<MapQualityRunDescriptor> InsertRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MapQualityWrite write,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO quality.map_quality_runs
                (id, map_snapshot_id, war_region_id,
                 validation_fetch_id, taxonomy_version,
                 quality_policy_version, baseline_map_observation_id,
                 decision, started_at, completed_at)
            VALUES
                (@id, @map_snapshot_id, @war_region_id,
                 @validation_fetch_id, @taxonomy_version,
                 @quality_policy_version, @baseline_map_observation_id,
                 @decision, @started_at, @completed_at)
            RETURNING
                id, map_snapshot_id, war_region_id,
                validation_fetch_id, taxonomy_version,
                quality_policy_version, baseline_map_observation_id,
                decision, started_at, completed_at, created_at;
            """;

        AddUuid(command, "id", MapQualityRunId.New().Value);
        AddUuid(command, "map_snapshot_id", write.MapSnapshotId.Value);
        AddUuid(command, "war_region_id", write.WarRegionId.Value);
        AddUuid(
            command,
            "validation_fetch_id",
            write.ValidationFetchId.Value);
        AddText(command, "taxonomy_version", write.TaxonomyVersion);
        AddText(
            command,
            "quality_policy_version",
            write.QualityPolicyVersion);
        AddNullableUuid(
            command,
            "baseline_map_observation_id",
            write.BaselineMapObservationId?.Value);
        AddText(command, "decision", ToStorage(write.Decision));
        AddTimestamp(command, "started_at", write.StartedAt);
        AddTimestamp(command, "completed_at", write.CompletedAt);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CanonicalStateIntegrityException(
                "Map quality run insert returned no row.");
        }

        return ReadRun(reader);
    }

    private static async Task InsertFindingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MapQualityRunId runId,
        IReadOnlyList<MapQualityFindingCandidate> findings,
        CancellationToken cancellationToken)
    {
        foreach (var finding in findings)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO quality.map_quality_findings
                    (id, quality_run_id, rule_key, rule_version,
                     configuration_version, effect,
                     map_item_occurrence_id,
                     map_text_occurrence_id,
                     detail_code, input_metrics)
                VALUES
                    (@id, @quality_run_id, @rule_key, @rule_version,
                     @configuration_version, @effect,
                     @map_item_occurrence_id,
                     @map_text_occurrence_id,
                     @detail_code, @input_metrics);
                """;

            AddUuid(command, "id", MapQualityFindingId.New().Value);
            AddUuid(command, "quality_run_id", runId.Value);
            AddText(command, "rule_key", finding.RuleKey);
            AddText(command, "rule_version", finding.RuleVersion);
            AddText(
                command,
                "configuration_version",
                finding.ConfigurationVersion);
            AddText(command, "effect", ToStorage(finding.Effect));
            AddNullableUuid(
                command,
                "map_item_occurrence_id",
                finding.MapItemOccurrenceId?.Value);
            AddNullableUuid(
                command,
                "map_text_occurrence_id",
                finding.MapTextOccurrenceId?.Value);
            AddNullableText(
                command,
                "detail_code",
                finding.DetailCode);
            command.Parameters.Add(
                "input_metrics",
                NpgsqlDbType.Jsonb).Value =
                finding.InputMetricsJson;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<MapObservationDescriptor> InsertObservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MapQualityRunDescriptor run,
        MapSnapshotDescriptor snapshot,
        ValidationContext validation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO runtime.map_observations
                (id, war_region_id, map_snapshot_id,
                 quality_run_id, validation_fetch_id,
                 capability_kind, observed_at, source_updated_at)
            VALUES
                (@id, @war_region_id, @map_snapshot_id,
                 @quality_run_id, @validation_fetch_id,
                 @capability_kind, @observed_at, @source_updated_at)
            RETURNING
                id, war_region_id, map_snapshot_id,
                quality_run_id, validation_fetch_id,
                capability_kind, observed_at, source_updated_at,
                recorded_at;
            """;

        AddUuid(command, "id", MapObservationId.New().Value);
        AddUuid(command, "war_region_id", run.WarRegionId.Value);
        AddUuid(command, "map_snapshot_id", run.MapSnapshotId.Value);
        AddUuid(command, "quality_run_id", run.Id.Value);
        AddUuid(
            command,
            "validation_fetch_id",
            run.ValidationFetchId.Value);
        AddText(command, "capability_kind", ToStorage(snapshot.Kind));
        AddTimestamp(command, "observed_at", validation.RetrievedAt);
        AddNullableTimestamp(
            command,
            "source_updated_at",
            snapshot.SourceUpdatedAt);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CanonicalStateIntegrityException(
                "Accepted map observation insert returned no row.");
        }

        return ReadObservation(reader);
    }

    private static void EnsureAcceptedSourceRegionCompatibility(
        MapSnapshotDescriptor snapshot,
        WarRegionDescriptor warRegion)
    {
        if (snapshot.SourceRegionId is { } supplied &&
            warRegion.SourceRegionId is { } existing &&
            supplied != existing)
        {
            throw new CanonicalStateIntegrityException(
                $"Accepted map snapshot sourceRegionId {supplied} conflicts with WarRegion sourceRegionId {existing}.");
        }
    }

    private static async Task<WarRegionDescriptor> EnrichSourceRegionIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarRegionDescriptor warRegion,
        int? sourceRegionId,
        CancellationToken cancellationToken)
    {
        if (sourceRegionId is null ||
            warRegion.SourceRegionId == sourceRegionId)
        {
            return warRegion;
        }

        if (warRegion.SourceRegionId is not null)
        {
            throw new CanonicalStateIntegrityException(
                "WarRegion sourceRegionId changed during accepted quality transaction.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE runtime.war_regions
            SET source_region_id = @source_region_id
            WHERE id = @id
              AND source_region_id IS NULL
            RETURNING
                id, war_id, region_id, source_map_name,
                source_region_id, first_seen_at, last_seen_at,
                created_at;
            """;

        AddInteger(
            command,
            "source_region_id",
            sourceRegionId.Value);
        AddUuid(command, "id", warRegion.Id.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CanonicalStateIntegrityException(
                "WarRegion sourceRegionId enrichment lost its locked row.");
        }

        return ReadWarRegion(reader);
    }

    private static async Task<MapQualityResult> BuildResultAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        MapQualityRunDescriptor run,
        CancellationToken cancellationToken)
    {
        var findings = await GetFindingsAsync(
            connection,
            transaction,
            run.Id,
            cancellationToken);
        var observation = await GetObservationAsync(
            connection,
            transaction,
            run.Id,
            cancellationToken);
        var warRegion = await GetWarRegionAsync(
            connection,
            transaction,
            run.WarRegionId,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"Quality run {run.Id} references missing WarRegion {run.WarRegionId}.");

        var result = new MapQualityResult(
            run,
            findings,
            observation,
            warRegion);
        EnsureDecisionObservationInvariant(result);
        return result;
    }

    private static async Task<IReadOnlyList<MapQualityFindingDescriptor>>
        GetFindingsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            MapQualityRunId runId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                id, quality_run_id, rule_key, rule_version,
                configuration_version, effect,
                map_item_occurrence_id,
                map_text_occurrence_id,
                detail_code, input_metrics::text, created_at
            FROM quality.map_quality_findings
            WHERE quality_run_id = @quality_run_id
            ORDER BY
                rule_key, rule_version, configuration_version,
                map_item_occurrence_id NULLS FIRST,
                map_text_occurrence_id NULLS FIRST,
                detail_code NULLS FIRST,
                id;
            """;

        AddUuid(command, "quality_run_id", runId.Value);

        var results =
            new List<MapQualityFindingDescriptor>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new MapQualityFindingDescriptor(
                    new MapQualityFindingId(reader.GetGuid(0)),
                    new MapQualityRunId(reader.GetGuid(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    ParseEffect(reader.GetString(5)),
                    reader.IsDBNull(6)
                        ? null
                        : new MapItemOccurrenceId(
                            reader.GetGuid(6)),
                    reader.IsDBNull(7)
                        ? null
                        : new MapTextOccurrenceId(
                            reader.GetGuid(7)),
                    reader.IsDBNull(8)
                        ? null
                        : reader.GetString(8),
                    reader.GetString(9),
                    reader.GetFieldValue<DateTimeOffset>(10)));
        }

        return results;
    }

    private static async Task<MapObservationDescriptor?> GetObservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        MapQualityRunId runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                id, war_region_id, map_snapshot_id,
                quality_run_id, validation_fetch_id,
                capability_kind, observed_at, source_updated_at,
                recorded_at
            FROM runtime.map_observations
            WHERE quality_run_id = @quality_run_id;
            """;

        AddUuid(command, "quality_run_id", runId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadObservation(reader);
    }

    private static async Task<WarRegionDescriptor?> GetWarRegionAsync(
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
                id, war_id, region_id, source_map_name,
                source_region_id, first_seen_at, last_seen_at,
                created_at
            FROM runtime.war_regions
            WHERE id = @id;
            """;
        AddUuid(command, "id", warRegionId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadWarRegion(reader);
    }

    private static void EnsureRunEquivalent(
        MapQualityRunDescriptor existing,
        MapQualityWrite supplied)
    {
        if (existing.MapSnapshotId != supplied.MapSnapshotId ||
            existing.WarRegionId != supplied.WarRegionId ||
            existing.ValidationFetchId != supplied.ValidationFetchId ||
            !string.Equals(
                existing.TaxonomyVersion,
                supplied.TaxonomyVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                existing.QualityPolicyVersion,
                supplied.QualityPolicyVersion,
                StringComparison.Ordinal) ||
            existing.BaselineMapObservationId !=
                supplied.BaselineMapObservationId ||
            existing.Decision != supplied.Decision)
        {
            throw new CanonicalStateIntegrityException(
                "Repeated map quality write differs from durable terminal quality state.");
        }
    }

    private static void EnsureFindingsEquivalent(
        IReadOnlyList<MapQualityFindingDescriptor> durable,
        IReadOnlyList<MapQualityFindingCandidate> supplied)
    {
        if (durable.Count != supplied.Count)
        {
            throw new CanonicalStateIntegrityException(
                "Repeated map quality write has a different finding count.");
        }

        var durableKeys = durable
            .Select(ToSemanticKey)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var suppliedKeys = supplied
            .Select(ToSemanticKey)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        if (!durableKeys.SequenceEqual(
                suppliedKeys,
                StringComparer.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Repeated map quality findings differ from durable state.");
        }
    }

    private static string ToSemanticKey(
        MapQualityFindingDescriptor finding) =>
        string.Join(
            "",
            finding.RuleKey,
            finding.RuleVersion,
            finding.ConfigurationVersion,
            ToStorage(finding.Effect),
            finding.MapItemOccurrenceId?.ToString() ?? string.Empty,
            finding.MapTextOccurrenceId?.ToString() ?? string.Empty,
            finding.DetailCode ?? string.Empty,
            CanonicalizeJsonObject(finding.InputMetricsJson));

    private static string ToSemanticKey(
        MapQualityFindingCandidate finding) =>
        string.Join(
            "",
            finding.RuleKey,
            finding.RuleVersion,
            finding.ConfigurationVersion,
            ToStorage(finding.Effect),
            finding.MapItemOccurrenceId?.ToString() ?? string.Empty,
            finding.MapTextOccurrenceId?.ToString() ?? string.Empty,
            finding.DetailCode ?? string.Empty,
            CanonicalizeJsonObject(finding.InputMetricsJson));

    private static string CanonicalizeJsonObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalJson(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalJson(
        Utf8JsonWriter writer,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                             .EnumerateObject()
                             .OrderBy(
                                 item => item.Name,
                                 StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void EnsureDecisionObservationInvariant(
        MapQualityResult result)
    {
        if (result.Run.Decision == MapQualityDecision.Accepted)
        {
            if (result.Observation is null)
            {
                throw new CanonicalStateIntegrityException(
                    $"Accepted quality run {result.Run.Id} has no runtime map observation.");
            }

            if (result.Observation.QualityRunId != result.Run.Id ||
                result.Observation.MapSnapshotId !=
                    result.Run.MapSnapshotId ||
                result.Observation.WarRegionId !=
                    result.Run.WarRegionId ||
                result.Observation.ValidationFetchId !=
                    result.Run.ValidationFetchId)
            {
                throw new CanonicalStateIntegrityException(
                    "Accepted runtime map observation does not match its quality-run binding.");
            }
        }
        else if (result.Observation is not null)
        {
            throw new CanonicalStateIntegrityException(
                $"Non-accepted quality run {result.Run.Id} has a runtime map observation.");
        }
    }

    private static MapSnapshotDescriptor ReadSnapshot(
        NpgsqlDataReader reader) =>
        new(
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

    private static WarRegionDescriptor ReadWarRegion(
        NpgsqlDataReader reader) =>
        new(
            new WarRegionId(reader.GetGuid(0)),
            new WarId(reader.GetGuid(1)),
            new RegionId(reader.GetGuid(2)),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));

    private static MapQualityRunDescriptor ReadRun(
        NpgsqlDataReader reader) =>
        new(
            new MapQualityRunId(reader.GetGuid(0)),
            new MapSnapshotId(reader.GetGuid(1)),
            new WarRegionId(reader.GetGuid(2)),
            new FetchId(reader.GetGuid(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6)
                ? null
                : new MapObservationId(reader.GetGuid(6)),
            ParseDecision(reader.GetString(7)),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetFieldValue<DateTimeOffset>(10));

    private static MapObservationDescriptor ReadObservation(
        NpgsqlDataReader reader) =>
        new(
            new MapObservationId(reader.GetGuid(0)),
            new WarRegionId(reader.GetGuid(1)),
            new MapSnapshotId(reader.GetGuid(2)),
            new MapQualityRunId(reader.GetGuid(3)),
            new FetchId(reader.GetGuid(4)),
            ParseKind(reader.GetString(5)),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetFieldValue<DateTimeOffset>(8));

    private static MapQualityDecision ParseDecision(string value) =>
        value switch
        {
            "accepted" => MapQualityDecision.Accepted,
            "suspect" => MapQualityDecision.Suspect,
            "quarantined" => MapQualityDecision.Quarantined,
            _ => throw new CanonicalStateIntegrityException(
                $"Unknown durable map quality decision '{value}'."),
        };

    private static MapQualityEffect ParseEffect(string value) =>
        value switch
        {
            "informational" => MapQualityEffect.Informational,
            "suspect" => MapQualityEffect.Suspect,
            "quarantined" => MapQualityEffect.Quarantined,
            _ => throw new CanonicalStateIntegrityException(
                $"Unknown durable map quality effect '{value}'."),
        };

    private static string ToStorage(MapQualityDecision value) =>
        value switch
        {
            MapQualityDecision.Accepted => "accepted",
            MapQualityDecision.Suspect => "suspect",
            MapQualityDecision.Quarantined => "quarantined",
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Unknown map quality decision."),
        };

    private static string ToStorage(MapQualityEffect value) =>
        value switch
        {
            MapQualityEffect.Informational => "informational",
            MapQualityEffect.Suspect => "suspect",
            MapQualityEffect.Quarantined => "quarantined",
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Unknown map quality effect."),
        };

    private static string ToStorage(MapSnapshotKind value) =>
        value switch
        {
            MapSnapshotKind.Static => "static",
            MapSnapshotKind.Dynamic => "dynamic",
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
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

    private static void AddNullableUuid(
        NpgsqlCommand command,
        string name,
        Guid? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value =
            value is null ? DBNull.Value : value.Value;

    private static void AddInteger(
        NpgsqlCommand command,
        string name,
        int value) =>
        command.Parameters.Add(name, NpgsqlDbType.Integer).Value = value;

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

    private sealed record WarRegionLockContext(
        WarRegionDescriptor WarRegion,
        ShardId ShardId);

    private sealed record ValidationContext(
        FetchId ValidationFetchId,
        EndpointId EndpointId,
        DateTimeOffset RetrievedAt,
        int? StatusCode,
        PayloadId? ValidationPayloadId,
        FetchId? PriorFetchId,
        string? AttemptOutcomeCode,
        EndpointId RepresentationEndpointId,
        PayloadId? RepresentationPayloadId,
        ShardId ShardId,
        string CapabilityKey,
        string SemanticKey);
}
