using System.Data;
using System.Text.Json;
using FoxData.Application.Canonical;
using FoxData.Application.Quality;
using FoxData.Core.Evidence;
using FoxData.Core.Quality;
using FoxData.Core.Runtime;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Quality;

public sealed class PostgresMapQualityStore(NpgsqlDataSource dataSource)
    : IMapQualityStore
{
    private const string RunColumns =
        """
        id, map_snapshot_id, war_region_id, validation_fetch_id,
        taxonomy_version, quality_policy_version,
        baseline_map_observation_id, decision,
        started_at, completed_at, created_at
        """;

    private const string FindingColumns =
        """
        id, quality_run_id, map_snapshot_id,
        rule_key, rule_version, configuration_version, effect,
        map_item_occurrence_id, map_text_occurrence_id,
        detail_code, input_metrics::text, created_at
        """;

    private const string ObservationColumns =
        """
        id, war_region_id, map_snapshot_id, quality_run_id,
        validation_fetch_id, capability_kind, observed_at,
        source_updated_at, recorded_at
        """;

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

        var run = await GetRunAsync(
            connection,
            transaction: null,
            mapSnapshotId,
            warRegionId,
            validationFetchId,
            taxonomyVersion,
            qualityPolicyVersion,
            forUpdate: false,
            cancellationToken);

        if (run is null)
        {
            return null;
        }

        return await LoadResultAsync(
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

        var warRegion = await LockWarRegionAsync(
            connection,
            transaction,
            write.WarRegionId,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"WarRegion {write.WarRegionId} does not exist.");

        var snapshot = await GetSnapshotContextAsync(
            connection,
            transaction,
            write.MapSnapshotId,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"Map snapshot {write.MapSnapshotId} does not exist.");

        var validation = await GetValidationFetchAsync(
            connection,
            transaction,
            write.ValidationFetchId,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"Validation Fetch {write.ValidationFetchId} does not exist.");

        EnsureBindingProvenance(
            write,
            warRegion,
            snapshot,
            validation);

        if (write.BaselineMapObservationId is { } baselineId)
        {
            await EnsureBaselineAsync(
                connection,
                transaction,
                baselineId,
                write,
                cancellationToken);
        }

        var existing = await GetRunAsync(
            connection,
            transaction,
            write.MapSnapshotId,
            write.WarRegionId,
            write.ValidationFetchId,
            write.TaxonomyVersion,
            write.QualityPolicyVersion,
            forUpdate: true,
            cancellationToken);

        if (existing is not null)
        {
            var result = await LoadResultAsync(
                connection,
                transaction,
                existing,
                cancellationToken);
            EnsureEquivalent(
                result,
                write,
                snapshot.SourceRegionId,
                warRegion.SourceRegionId);

            await transaction.CommitAsync(cancellationToken);
            return result;
        }

        var run = await InsertRunAsync(
            connection,
            transaction,
            write,
            cancellationToken);
        var findings = await InsertFindingsAsync(
            connection,
            transaction,
            run.Id,
            write.MapSnapshotId,
            write.Findings,
            cancellationToken);

        MapObservationDescriptor? observation = null;
        if (write.Decision == MapQualityDecision.Accepted)
        {
            await EnrichWarRegionSourceIdAsync(
                connection,
                transaction,
                warRegion,
                snapshot.SourceRegionId,
                cancellationToken);

            observation = await InsertObservationAsync(
                connection,
                transaction,
                run,
                snapshot,
                write,
                cancellationToken);
        }

        var resultToReturn = new MapQualityResult(
            run,
            findings,
            observation);

        EnsureTerminalShape(resultToReturn);

        await transaction.CommitAsync(cancellationToken);
        return resultToReturn;
    }

    private static async Task<WarRegionContext?> LockWarRegionAsync(
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
                war.shard_id,
                membership.source_map_name,
                membership.source_region_id,
                membership.first_seen_at,
                membership.last_seen_at
            FROM runtime.war_regions AS membership
            INNER JOIN runtime.wars AS war
                ON war.id = membership.war_id
            WHERE membership.id = @war_region_id
            FOR UPDATE OF membership;
            """;
        AddUuid(command, "war_region_id", warRegionId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WarRegionContext(
            new WarRegionId(reader.GetGuid(0)),
            new WarId(reader.GetGuid(1)),
            new ShardId(reader.GetGuid(2)),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6));
    }

    private static async Task<MapSnapshotContext?> GetSnapshotContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MapSnapshotId mapSnapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                snapshot.id,
                snapshot.representation_fetch_id,
                representation_fetch.endpoint_id,
                endpoint.shard_id,
                snapshot.capability_kind,
                snapshot.source_map_name,
                snapshot.source_region_id,
                snapshot.source_updated_at
            FROM evidence.map_snapshots AS snapshot
            INNER JOIN evidence.fetches AS representation_fetch
                ON representation_fetch.id =
                    snapshot.representation_fetch_id
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = representation_fetch.endpoint_id
            WHERE snapshot.id = @map_snapshot_id;
            """;
        AddUuid(command, "map_snapshot_id", mapSnapshotId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MapSnapshotContext(
            new MapSnapshotId(reader.GetGuid(0)),
            new FetchId(reader.GetGuid(1)),
            new EndpointId(reader.GetGuid(2)),
            new ShardId(reader.GetGuid(3)),
            ParseKind(reader.GetString(4)),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(7));
    }

    private static async Task<ValidationFetchContext?>
        GetValidationFetchAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            FetchId validationFetchId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                validation_fetch.id,
                validation_fetch.endpoint_id,
                endpoint.shard_id,
                validation_fetch.payload_id,
                validation_fetch.prior_fetch_id,
                validation_fetch.status_code,
                validation_fetch.body_error_code,
                validation_fetch.retrieved_at,
                attempt.outcome_code
            FROM evidence.fetches AS validation_fetch
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = validation_fetch.endpoint_id
            INNER JOIN ingest.attempts AS attempt
                ON attempt.id = validation_fetch.attempt_id
            WHERE validation_fetch.id = @validation_fetch_id;
            """;
        AddUuid(
            command,
            "validation_fetch_id",
            validationFetchId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ValidationFetchContext(
            new FetchId(reader.GetGuid(0)),
            new EndpointId(reader.GetGuid(1)),
            new ShardId(reader.GetGuid(2)),
            reader.IsDBNull(3)
                ? null
                : new PayloadId(reader.GetGuid(3)),
            reader.IsDBNull(4)
                ? null
                : new FetchId(reader.GetGuid(4)),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    private static void EnsureBindingProvenance(
        MapQualityWrite write,
        WarRegionContext warRegion,
        MapSnapshotContext snapshot,
        ValidationFetchContext validation)
    {
        if (snapshot.Kind != write.Kind ||
            snapshot.SourceUpdatedAt != write.SourceUpdatedAt)
        {
            throw new CanonicalStateIntegrityException(
                "Map quality write does not match its normalized snapshot kind/sourceUpdatedAt.");
        }

        if (!string.Equals(
                snapshot.SourceMapName,
                warRegion.SourceMapName,
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Map quality snapshot and WarRegion have different exact source map identities.");
        }

        if (snapshot.ShardId != warRegion.ShardId ||
            validation.ShardId != warRegion.ShardId)
        {
            throw new CanonicalStateIntegrityException(
                "Map quality snapshot/validation Fetch do not belong to the WarRegion shard.");
        }

        if (validation.EndpointId != snapshot.EndpointId)
        {
            throw new CanonicalStateIntegrityException(
                "Map quality validation Fetch belongs to a different source endpoint than its representation.");
        }

        if (!string.Equals(
                validation.AttemptOutcomeCode,
                "captured_current",
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Map quality validation Fetch is not authoritative captured_current evidence.");
        }

        if (validation.BodyErrorCode is not null)
        {
            throw new CanonicalStateIntegrityException(
                "Map quality validation Fetch contains a body-capture error.");
        }

        if (validation.RetrievedAt != write.ObservedAt)
        {
            throw new CanonicalStateIntegrityException(
                "Map quality observedAt must equal the authoritative validation Fetch retrievedAt boundary.");
        }

        if (warRegion.FirstSeenAt > write.ObservedAt)
        {
            throw new CanonicalStateIntegrityException(
                "Map quality observation predates the proven WarRegion membership boundary.");
        }

        if (validation.Id == snapshot.RepresentationFetchId)
        {
            if (validation.PayloadId is null)
            {
                throw new CanonicalStateIntegrityException(
                    "Body-bearing map quality validation has no durable Payload.");
            }

            return;
        }

        if (validation.StatusCode != 304 ||
            validation.PayloadId is not null ||
            validation.PriorFetchId !=
                snapshot.RepresentationFetchId)
        {
            throw new CanonicalStateIntegrityException(
                "Map quality 304 validation does not point to the exact normalized representation Fetch.");
        }
    }

    private static async Task EnsureBaselineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MapObservationId baselineId,
        MapQualityWrite write,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                observation.war_region_id,
                observation.capability_kind,
                observation.observed_at,
                run.decision
            FROM runtime.map_observations AS observation
            INNER JOIN quality.map_quality_runs AS run
                ON run.id = observation.quality_run_id
            WHERE observation.id = @baseline_id;
            """;
        AddUuid(command, "baseline_id", baselineId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CanonicalStateIntegrityException(
                $"Baseline map observation {baselineId} does not exist.");
        }

        var warRegionId = new WarRegionId(reader.GetGuid(0));
        var kind = ParseKind(reader.GetString(1));
        var observedAt = reader.GetFieldValue<DateTimeOffset>(2);
        var decision = ParseDecision(reader.GetString(3));

        if (warRegionId != write.WarRegionId ||
            kind != write.Kind ||
            decision != MapQualityDecision.Accepted ||
            observedAt > write.ObservedAt)
        {
            throw new CanonicalStateIntegrityException(
                "Map quality baseline does not belong to the same accepted WarRegion/capability stream before the candidate boundary.");
        }
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
            $"""
            INSERT INTO quality.map_quality_runs
                (id, map_snapshot_id, war_region_id,
                 validation_fetch_id, taxonomy_version,
                 quality_policy_version,
                 baseline_map_observation_id, decision,
                 started_at, completed_at)
            VALUES
                (@id, @map_snapshot_id, @war_region_id,
                 @validation_fetch_id, @taxonomy_version,
                 @quality_policy_version,
                 @baseline_map_observation_id, @decision,
                 @started_at, @completed_at)
            RETURNING {RunColumns};
            """;

        AddUuid(command, "id", MapQualityRunId.New().Value);
        AddUuid(
            command,
            "map_snapshot_id",
            write.MapSnapshotId.Value);
        AddUuid(
            command,
            "war_region_id",
            write.WarRegionId.Value);
        AddUuid(
            command,
            "validation_fetch_id",
            write.ValidationFetchId.Value);
        AddText(
            command,
            "taxonomy_version",
            write.TaxonomyVersion);
        AddText(
            command,
            "quality_policy_version",
            write.QualityPolicyVersion);
        AddNullableUuid(
            command,
            "baseline_map_observation_id",
            write.BaselineMapObservationId?.Value);
        AddText(
            command,
            "decision",
            ToStorage(write.Decision));
        AddTimestamp(command, "started_at", write.StartedAt);
        AddTimestamp(command, "completed_at", write.CompletedAt);

        return await ReadRunAsync(command, cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "Inserted map quality run was not returned.");
    }

    private static async Task<IReadOnlyList<MapQualityFindingDescriptor>>
        InsertFindingsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            MapQualityRunId runId,
            MapSnapshotId mapSnapshotId,
            IReadOnlyList<MapQualityFindingCandidate> findings,
            CancellationToken cancellationToken)
    {
        var results =
            new List<MapQualityFindingDescriptor>(findings.Count);

        foreach (var finding in findings)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"""
                INSERT INTO quality.map_quality_findings
                    (id, quality_run_id, map_snapshot_id,
                     rule_key, rule_version,
                     configuration_version, effect,
                     map_item_occurrence_id,
                     map_text_occurrence_id,
                     detail_code, input_metrics)
                VALUES
                    (@id, @quality_run_id, @map_snapshot_id,
                     @rule_key, @rule_version,
                     @configuration_version, @effect,
                     @map_item_occurrence_id,
                     @map_text_occurrence_id,
                     @detail_code, @input_metrics)
                RETURNING {FindingColumns};
                """;

            AddUuid(
                command,
                "id",
                MapQualityFindingId.New().Value);
            AddUuid(
                command,
                "quality_run_id",
                runId.Value);
            AddUuid(
                command,
                "map_snapshot_id",
                mapSnapshotId.Value);
            AddText(command, "rule_key", finding.RuleKey);
            AddText(
                command,
                "rule_version",
                finding.RuleVersion);
            AddText(
                command,
                "configuration_version",
                finding.ConfigurationVersion);
            AddText(command, "effect", finding.Effect);
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
                    NpgsqlDbType.Jsonb)
                .Value = finding.InputMetricsJson;

            var descriptor = await ReadFindingAsync(
                command,
                cancellationToken)
                ?? throw new CanonicalStateIntegrityException(
                    "Inserted map quality finding was not returned.");

            results.Add(descriptor);
        }

        return results;
    }

    private static async Task EnrichWarRegionSourceIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarRegionContext warRegion,
        int? snapshotSourceRegionId,
        CancellationToken cancellationToken)
    {
        if (snapshotSourceRegionId is null)
        {
            return;
        }

        if (warRegion.SourceRegionId is { } existing)
        {
            if (existing != snapshotSourceRegionId.Value)
            {
                throw new CanonicalStateIntegrityException(
                    "Accepted map quality result conflicts with the durable WarRegion sourceRegionId.");
            }

            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE runtime.war_regions
            SET source_region_id = @source_region_id
            WHERE id = @war_region_id
              AND source_region_id IS NULL;
            """;
        command.Parameters.Add(
                "source_region_id",
                NpgsqlDbType.Integer)
            .Value = snapshotSourceRegionId.Value;
        AddUuid(
            command,
            "war_region_id",
            warRegion.Id.Value);

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new CanonicalStateIntegrityException(
                "WarRegion sourceRegionId enrichment lost its locked null precondition.");
        }
    }

    private static async Task<MapObservationDescriptor>
        InsertObservationAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            MapQualityRunDescriptor run,
            MapSnapshotContext snapshot,
            MapQualityWrite write,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            INSERT INTO runtime.map_observations
                (id, war_region_id, map_snapshot_id,
                 quality_run_id, validation_fetch_id,
                 capability_kind, observed_at,
                 source_updated_at, quality_decision)
            VALUES
                (@id, @war_region_id, @map_snapshot_id,
                 @quality_run_id, @validation_fetch_id,
                 @capability_kind, @observed_at,
                 @source_updated_at, 'accepted')
            RETURNING {ObservationColumns};
            """;

        AddUuid(
            command,
            "id",
            MapObservationId.New().Value);
        AddUuid(
            command,
            "war_region_id",
            write.WarRegionId.Value);
        AddUuid(
            command,
            "map_snapshot_id",
            write.MapSnapshotId.Value);
        AddUuid(
            command,
            "quality_run_id",
            run.Id.Value);
        AddUuid(
            command,
            "validation_fetch_id",
            write.ValidationFetchId.Value);
        AddText(
            command,
            "capability_kind",
            ToStorage(snapshot.Kind));
        AddTimestamp(
            command,
            "observed_at",
            write.ObservedAt);
        AddNullableTimestamp(
            command,
            "source_updated_at",
            snapshot.SourceUpdatedAt);

        return await ReadObservationAsync(
            command,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "Inserted accepted map observation was not returned.");
    }

    private static async Task<MapQualityRunDescriptor?> GetRunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        MapSnapshotId mapSnapshotId,
        WarRegionId warRegionId,
        FetchId validationFetchId,
        string taxonomyVersion,
        string qualityPolicyVersion,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {RunColumns}
            FROM quality.map_quality_runs
            WHERE map_snapshot_id = @map_snapshot_id
              AND war_region_id = @war_region_id
              AND validation_fetch_id = @validation_fetch_id
              AND taxonomy_version = @taxonomy_version
              AND quality_policy_version =
                  @quality_policy_version
            {(forUpdate ? "FOR UPDATE" : string.Empty)};
            """;

        AddUuid(
            command,
            "map_snapshot_id",
            mapSnapshotId.Value);
        AddUuid(
            command,
            "war_region_id",
            warRegionId.Value);
        AddUuid(
            command,
            "validation_fetch_id",
            validationFetchId.Value);
        AddText(
            command,
            "taxonomy_version",
            taxonomyVersion);
        AddText(
            command,
            "quality_policy_version",
            qualityPolicyVersion);

        return await ReadRunAsync(command, cancellationToken);
    }

    private static async Task<MapQualityResult> LoadResultAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        MapQualityRunDescriptor run,
        CancellationToken cancellationToken)
    {
        var findings = await GetFindingsAsync(
            connection,
            transaction,
            run.Id,
            run.MapSnapshotId,
            cancellationToken);
        var observation = await GetObservationAsync(
            connection,
            transaction,
            run.Id,
            cancellationToken);

        var result = new MapQualityResult(
            run,
            findings,
            observation);
        EnsureTerminalShape(result);

        return result;
    }

    private static async Task<IReadOnlyList<MapQualityFindingDescriptor>>
        GetFindingsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            MapQualityRunId runId,
            MapSnapshotId mapSnapshotId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {FindingColumns}
            FROM quality.map_quality_findings
            WHERE quality_run_id = @quality_run_id
            ORDER BY rule_key, id;
            """;
        AddUuid(
            command,
            "quality_run_id",
            runId.Value);

        var results = new List<MapQualityFindingDescriptor>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            if (new MapSnapshotId(reader.GetGuid(2)) != mapSnapshotId)
            {
                throw new CanonicalStateIntegrityException(
                    "Durable quality finding references a different map snapshot than its QualityRun.");
            }

            results.Add(ReadFinding(reader));
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
            $"""
            SELECT {ObservationColumns}
            FROM runtime.map_observations
            WHERE quality_run_id = @quality_run_id;
            """;
        AddUuid(
            command,
            "quality_run_id",
            runId.Value);

        return await ReadObservationAsync(
            command,
            cancellationToken);
    }

    private static void EnsureTerminalShape(
        MapQualityResult result)
    {
        if (result.Run.Decision == MapQualityDecision.Accepted)
        {
            if (result.Observation is null)
            {
                throw new CanonicalStateIntegrityException(
                    $"Accepted map quality run {result.Run.Id} has no runtime map observation.");
            }

            return;
        }

        if (result.Observation is not null)
        {
            throw new CanonicalStateIntegrityException(
                $"Non-accepted map quality run {result.Run.Id} unexpectedly has a runtime map observation.");
        }
    }

    private static void EnsureEquivalent(
        MapQualityResult existing,
        MapQualityWrite supplied,
        int? snapshotSourceRegionId,
        int? currentWarRegionSourceRegionId)
    {
        var run = existing.Run;
        if (run.MapSnapshotId != supplied.MapSnapshotId ||
            run.WarRegionId != supplied.WarRegionId ||
            run.ValidationFetchId != supplied.ValidationFetchId ||
            !string.Equals(
                run.TaxonomyVersion,
                supplied.TaxonomyVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                run.QualityPolicyVersion,
                supplied.QualityPolicyVersion,
                StringComparison.Ordinal) ||
            run.BaselineMapObservationId !=
                supplied.BaselineMapObservationId ||
            run.Decision != supplied.Decision)
        {
            throw new CanonicalStateIntegrityException(
                "Repeated map quality write differs from the durable QualityRun.");
        }

        if (!FindingsEquivalent(
                existing.Findings,
                supplied.Findings))
        {
            throw new CanonicalStateIntegrityException(
                "Repeated map quality write differs from the durable quality findings.");
        }

        if (supplied.Decision == MapQualityDecision.Accepted)
        {
            var observation = existing.Observation
                ?? throw new CanonicalStateIntegrityException(
                    "Accepted durable quality run has no observation.");

            if (observation.WarRegionId !=
                    supplied.WarRegionId ||
                observation.MapSnapshotId !=
                    supplied.MapSnapshotId ||
                observation.ValidationFetchId !=
                    supplied.ValidationFetchId ||
                observation.Kind != supplied.Kind ||
                observation.ObservedAt !=
                    supplied.ObservedAt ||
                observation.SourceUpdatedAt !=
                    supplied.SourceUpdatedAt)
            {
                throw new CanonicalStateIntegrityException(
                    "Repeated accepted map quality write differs from the durable map observation.");
            }

            if (snapshotSourceRegionId is { } sourceRegionId &&
                currentWarRegionSourceRegionId != sourceRegionId)
            {
                throw new CanonicalStateIntegrityException(
                    "Accepted durable quality run is missing its sourceRegionId enrichment.");
            }
        }
    }

    private static bool FindingsEquivalent(
        IReadOnlyList<MapQualityFindingDescriptor> existing,
        IReadOnlyList<MapQualityFindingCandidate> supplied)
    {
        if (existing.Count != supplied.Count)
        {
            return false;
        }

        var matched = new bool[existing.Count];
        foreach (var candidate in supplied)
        {
            var found = false;
            for (var index = 0; index < existing.Count; index++)
            {
                if (matched[index] ||
                    !FindingEquivalent(
                        existing[index],
                        candidate))
                {
                    continue;
                }

                matched[index] = true;
                found = true;
                break;
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static bool FindingEquivalent(
        MapQualityFindingDescriptor existing,
        MapQualityFindingCandidate supplied)
    {
        if (!string.Equals(
                existing.RuleKey,
                supplied.RuleKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                existing.RuleVersion,
                supplied.RuleVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                existing.ConfigurationVersion,
                supplied.ConfigurationVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                existing.Effect,
                supplied.Effect,
                StringComparison.Ordinal) ||
            existing.MapItemOccurrenceId !=
                supplied.MapItemOccurrenceId ||
            existing.MapTextOccurrenceId !=
                supplied.MapTextOccurrenceId ||
            !string.Equals(
                existing.DetailCode,
                supplied.DetailCode,
                StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var left =
                JsonDocument.Parse(existing.InputMetricsJson);
            using var right =
                JsonDocument.Parse(supplied.InputMetricsJson);

            return JsonElement.DeepEquals(
                left.RootElement,
                right.RootElement);
        }
        catch (JsonException exception)
        {
            throw new CanonicalStateIntegrityException(
                $"Durable quality finding contains invalid JSON: {exception.Message}");
        }
    }

    private static async Task<MapQualityRunDescriptor?> ReadRunAsync(
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

        return new MapQualityRunDescriptor(
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
    }

    private static async Task<MapQualityFindingDescriptor?>
        ReadFindingAsync(
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

        return ReadFinding(reader);
    }

    private static MapQualityFindingDescriptor ReadFinding(
        NpgsqlDataReader reader) =>
        new(
            new MapQualityFindingId(reader.GetGuid(0)),
            new MapQualityRunId(reader.GetGuid(1)),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7)
                ? null
                : new MapItemOccurrenceId(reader.GetGuid(7)),
            reader.IsDBNull(8)
                ? null
                : new MapTextOccurrenceId(reader.GetGuid(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetString(10),
            reader.GetFieldValue<DateTimeOffset>(11));

    private static async Task<MapObservationDescriptor?>
        ReadObservationAsync(
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

        return new MapObservationDescriptor(
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
    }

    private static MapQualityDecision ParseDecision(string value) =>
        value switch
        {
            "accepted" => MapQualityDecision.Accepted,
            "suspect" => MapQualityDecision.Suspect,
            "quarantined" => MapQualityDecision.Quarantined,
            _ => throw new CanonicalStateIntegrityException(
                $"Unknown durable map quality decision '{value}'."),
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

    private static MapSnapshotKind ParseKind(string value) =>
        value switch
        {
            "static" => MapSnapshotKind.Static,
            "dynamic" => MapSnapshotKind.Dynamic,
            _ => throw new CanonicalStateIntegrityException(
                $"Unknown durable map snapshot kind '{value}'."),
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

    private static void AddNullableTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset? value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value =
            value is null ? DBNull.Value : value.Value;

    private sealed record WarRegionContext(
        WarRegionId Id,
        WarId WarId,
        ShardId ShardId,
        string SourceMapName,
        int? SourceRegionId,
        DateTimeOffset FirstSeenAt,
        DateTimeOffset LastSeenAt);

    private sealed record MapSnapshotContext(
        MapSnapshotId Id,
        FetchId RepresentationFetchId,
        EndpointId EndpointId,
        ShardId ShardId,
        MapSnapshotKind Kind,
        string SourceMapName,
        int? SourceRegionId,
        DateTimeOffset? SourceUpdatedAt);

    private sealed record ValidationFetchContext(
        FetchId Id,
        EndpointId EndpointId,
        ShardId ShardId,
        PayloadId? PayloadId,
        FetchId? PriorFetchId,
        int? StatusCode,
        string? BodyErrorCode,
        DateTimeOffset RetrievedAt,
        string? AttemptOutcomeCode);
}
