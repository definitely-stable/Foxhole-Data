using System.Data;
using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Core.Quality;
using FoxData.Core.Runtime;
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
        var key = new MapQualityWrite(
            mapSnapshotId,
            warRegionId,
            validationFetchId,
            taxonomyVersion,
            qualityPolicyVersion,
            null,
            MapQualityDecision.Accepted,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            []);
        var run = await GetRunByIdentityAsync(
            connection,
            null,
            key,
            cancellationToken);
        return run is null
            ? null
            : await GetByRunIdAsync(run.Id, cancellationToken);
    }

    public async Task<MapQualityResult?> GetByRunIdAsync(
        MapQualityRunId runId,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);

        var run = await GetRunByIdAsync(
            connection,
            transaction: null,
            runId,
            cancellationToken);
        if (run is null)
        {
            return null;
        }

        var findings = await GetFindingsAsync(
            connection,
            transaction: null,
            run.Id,
            cancellationToken);
        var observation = await GetObservationAsync(
            connection,
            transaction: null,
            run.Id,
            cancellationToken);
        var warRegion = await GetWarRegionAsync(
            connection,
            transaction: null,
            run.WarRegionId,
            forUpdate: false,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"Map quality run {run.Id} references missing war-region {run.WarRegionId}.");

        EnsureDecisionObservationInvariant(run, observation);

        return new MapQualityResult(
            run,
            findings,
            observation,
            warRegion);
    }

    public async Task<MapQualityResult> RecordAsync(
        MapQualityWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        MapQualityDecisionSafety.Validate(write);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        var warRegion = await GetWarRegionAsync(
            connection,
            transaction,
            write.WarRegionId,
            forUpdate: true,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                $"War-region {write.WarRegionId} does not exist.");

        var context = await GetAndVerifyContextAsync(
            connection,
            transaction,
            write,
            warRegion,
            cancellationToken);

        var existing = await GetRunByIdentityAsync(
            connection,
            transaction,
            write,
            cancellationToken);

        if (existing is not null)
        {
            var existingFindings = await GetFindingsAsync(
                connection,
                transaction,
                existing.Id,
                cancellationToken);
            var existingObservation = await GetObservationAsync(
                connection,
                transaction,
                existing.Id,
                cancellationToken);

            EnsureEquivalent(
                existing,
                existingFindings,
                existingObservation,
                write,
                context);

            EnsureDecisionObservationInvariant(
                existing,
                existingObservation);

            await transaction.CommitAsync(cancellationToken);

            return new MapQualityResult(
                existing,
                existingFindings,
                existingObservation,
                warRegion);
        }

        await ValidateFindingReferencesAsync(
            connection,
            transaction,
            write.MapSnapshotId,
            write.Findings,
            cancellationToken);

        // Preliminary quality plans can become stale. Recompute the entire
        // authoritative Fetch barrier and baseline after acquiring this
        // WarRegion row lock, immediately before the immutable write.
        var ordering = await MapQualityOrderingQueries.GetPlanAsync(
            connection,
            transaction,
            write.MapSnapshotId,
            write.WarRegionId,
            write.ValidationFetchId,
            write.TaxonomyVersion,
            write.QualityPolicyVersion,
            cancellationToken);
        if (ordering.Status == MapQualityOrderingStatus.Deferred)
        {
            throw new MapQualityOrderingDeferredException(
                ordering.DeferredReason!);
        }

        if (ordering.ObservedAt != context.ValidationRetrievedAt ||
            ordering.Baseline?.Id != write.BaselineMapObservationId)
        {
            throw new MapQualityOrderingDeferredException(
                "quality_baseline_changed");
        }

        if (write.Decision == MapQualityDecision.Accepted)
        {
            ValidateAcceptedSourceRegion(
                warRegion,
                context.Snapshot.SourceRegionId);
        }

        var run = await InsertRunAsync(
            connection,
            transaction,
            write,
            cancellationToken);

        var findings = await InsertFindingsAsync(
            connection,
            transaction,
            run,
            write.MapSnapshotId,
            write.Findings,
            cancellationToken);

        MapObservationDescriptor? observation = null;
        if (write.Decision == MapQualityDecision.Accepted)
        {
            observation = await InsertObservationAsync(
                connection,
                transaction,
                run,
                context,
                cancellationToken);

            warRegion = await EnrichWarRegionAsync(
                connection,
                transaction,
                warRegion,
                context.Snapshot.SourceRegionId,
                cancellationToken);
        }

        EnsureDecisionObservationInvariant(
            run,
            observation);

        await transaction.CommitAsync(cancellationToken);

        return new MapQualityResult(
            run,
            findings,
            observation,
            warRegion);
    }

    private static async Task<QualityContext> GetAndVerifyContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MapQualityWrite write,
        WarRegionDescriptor warRegion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                snapshot.id,
                snapshot.normalization_run_id,
                snapshot.source_parse_run_id,
                snapshot.representation_fetch_id,
                snapshot.capability_kind,
                snapshot.source_map_name,
                snapshot.source_region_id,
                snapshot.source_scorched_victory_towns,
                snapshot.source_version,
                snapshot.source_last_updated_ms,
                snapshot.source_updated_at,
                snapshot.source_map_items_array_present,
                snapshot.source_map_text_items_array_present,
                snapshot.item_count,
                snapshot.text_item_count,
                snapshot.recorded_at,
                representation_fetch.endpoint_id,
                representation_fetch.payload_id,
                representation_attempt.outcome_code,
                endpoint.capability_key,
                endpoint.semantic_key,
                validation_fetch.endpoint_id,
                validation_fetch.status_code,
                validation_fetch.payload_id,
                validation_fetch.prior_fetch_id,
                validation_fetch.retrieved_at,
                validation_attempt.outcome_code,
                endpoint.shard_id,
                war.shard_id
            FROM evidence.map_snapshots AS snapshot
            INNER JOIN evidence.fetches AS representation_fetch
                ON representation_fetch.id =
                   snapshot.representation_fetch_id
            INNER JOIN ingest.attempts AS representation_attempt
                ON representation_attempt.id =
                   representation_fetch.attempt_id
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = representation_fetch.endpoint_id
            INNER JOIN runtime.wars AS war
                ON war.id = @war_id
            INNER JOIN evidence.fetches AS validation_fetch
                ON validation_fetch.id = @validation_fetch_id
            INNER JOIN ingest.attempts AS validation_attempt
                ON validation_attempt.id = validation_fetch.attempt_id
            WHERE snapshot.id = @map_snapshot_id;
            """;

        AddUuid(command, "map_snapshot_id", write.MapSnapshotId.Value);
        AddUuid(command, "validation_fetch_id", write.ValidationFetchId.Value);
        AddUuid(command, "war_id", warRegion.WarId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CanonicalStateIntegrityException(
                $"Map snapshot {write.MapSnapshotId} or validation Fetch {write.ValidationFetchId} does not exist.");
        }

        var snapshot = new MapSnapshotDescriptor(
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

        var representationEndpointId = reader.GetGuid(16);
        var representationHasPayload = !reader.IsDBNull(17);
        var representationOutcome =
            reader.IsDBNull(18) ? null : reader.GetString(18);
        var endpointCapabilityKey = reader.GetString(19);
        var endpointSemanticKey = reader.GetString(20);
        var validationEndpointId = reader.GetGuid(21);
        int? validationStatus = reader.IsDBNull(22)
            ? null
            : reader.GetInt32(22);
        var validationHasPayload = !reader.IsDBNull(23);
        FetchId? validationPriorFetchId = reader.IsDBNull(24)
            ? null
            : new FetchId(reader.GetGuid(24));
        var validationRetrievedAt =
            reader.GetFieldValue<DateTimeOffset>(25);
        var validationOutcome =
            reader.IsDBNull(26) ? null : reader.GetString(26);
        var sourceShardId = reader.GetGuid(27);
        var warShardId = reader.GetGuid(28);

        if (sourceShardId != warShardId ||
            validationRetrievedAt < warRegion.FirstSeenAt)
        {
            throw new CanonicalStateIntegrityException(
                "Map quality validation belongs to a different shard or precedes proven WarRegion membership.");
        }

        if (!string.Equals(
                representationOutcome,
                "captured_current",
                StringComparison.Ordinal) ||
            !string.Equals(
                validationOutcome,
                "captured_current",
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Map quality requires authoritative captured_current representation and validation Fetches.");
        }

        if (!representationHasPayload)
        {
            throw new CanonicalStateIntegrityException(
                "Map quality snapshot representation Fetch is not body-bearing.");
        }

        if (representationEndpointId != validationEndpointId)
        {
            throw new CanonicalStateIntegrityException(
                "Map quality validation Fetch belongs to a different endpoint than the normalized representation.");
        }

        var expectedCapability = snapshot.Kind switch
        {
            MapSnapshotKind.Static => "static-map-state",
            MapSnapshotKind.Dynamic => "dynamic-map-state",
            _ => throw new CanonicalStateIntegrityException(
                $"Unsupported map snapshot kind '{snapshot.Kind}'."),
        };
        var expectedSemantic = snapshot.Kind switch
        {
            MapSnapshotKind.Static =>
                $"map-static/{snapshot.SourceMapName}",
            MapSnapshotKind.Dynamic =>
                $"map-dynamic/{snapshot.SourceMapName}",
            _ => throw new CanonicalStateIntegrityException(
                $"Unsupported map snapshot kind '{snapshot.Kind}'."),
        };

        if (!string.Equals(
                endpointCapabilityKey,
                expectedCapability,
                StringComparison.Ordinal) ||
            !string.Equals(
                endpointSemanticKey,
                expectedSemantic,
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Map quality snapshot endpoint provenance does not match its kind/source map identity.");
        }

        if (!string.Equals(
                warRegion.SourceMapName,
                snapshot.SourceMapName,
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Map quality snapshot source map identity does not match the target WarRegion.");
        }

        var representationFetchId = snapshot.RepresentationFetchId;
        var validationFetchId = write.ValidationFetchId;

        if (validationStatus == 200)
        {
            if (validationFetchId != representationFetchId ||
                !validationHasPayload)
            {
                throw new CanonicalStateIntegrityException(
                    "A body-bearing quality validation must be the exact normalized representation Fetch.");
            }
        }
        else if (validationStatus == 304)
        {
            if (validationHasPayload ||
                validationFetchId == representationFetchId ||
                validationPriorFetchId != representationFetchId)
            {
                throw new CanonicalStateIntegrityException(
                    "A 304 quality validation must directly reference the exact body-bearing representation Fetch.");
            }
        }
        else
        {
            throw new CanonicalStateIntegrityException(
                $"Map quality validation Fetch has unsupported HTTP status {validationStatus?.ToString() ?? "NULL"}.");
        }

        return new QualityContext(
            snapshot,
            validationRetrievedAt);
    }

    private static async Task ValidateFindingReferencesAsync(
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
                await EnsureOccurrenceBelongsToSnapshotAsync(
                    connection,
                    transaction,
                    "evidence.map_item_occurrences",
                    itemId.Value,
                    snapshotId.Value,
                    cancellationToken);
            }

            if (finding.MapTextOccurrenceId is { } textId)
            {
                await EnsureOccurrenceBelongsToSnapshotAsync(
                    connection,
                    transaction,
                    "evidence.map_text_occurrences",
                    textId.Value,
                    snapshotId.Value,
                    cancellationToken);
            }
        }
    }

    private static async Task EnsureOccurrenceBelongsToSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        Guid occurrenceId,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT EXISTS (
                SELECT 1
                FROM {table}
                WHERE id = @occurrence_id
                  AND map_snapshot_id = @map_snapshot_id);
            """;
        AddUuid(command, "occurrence_id", occurrenceId);
        AddUuid(command, "map_snapshot_id", snapshotId);

        if (await command.ExecuteScalarAsync(cancellationToken)
            is not true)
        {
            throw new CanonicalStateIntegrityException(
                "Map quality finding occurrence reference does not belong to the target map snapshot.");
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

        return await ReadRunAsync(command, cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "Inserted map quality run was not returned.");
    }

    private static async Task<IReadOnlyList<MapQualityFindingDescriptor>>
        InsertFindingsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            MapQualityRunDescriptor run,
            MapSnapshotId snapshotId,
            IReadOnlyList<MapQualityFindingCandidate> candidates,
            CancellationToken cancellationToken)
    {
        var results =
            new List<MapQualityFindingDescriptor>(candidates.Count);

        foreach (var candidate in candidates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO quality.map_quality_findings
                    (id, quality_run_id, map_snapshot_id,
                     rule_key, rule_version, configuration_version,
                     effect, map_item_occurrence_id,
                     map_text_occurrence_id, detail_code,
                     input_metrics)
                VALUES
                    (@id, @quality_run_id, @map_snapshot_id,
                     @rule_key, @rule_version, @configuration_version,
                     @effect, @map_item_occurrence_id,
                     @map_text_occurrence_id, @detail_code,
                     @input_metrics)
                RETURNING
                    id, quality_run_id, map_snapshot_id,
                    rule_key, rule_version, configuration_version,
                    effect, map_item_occurrence_id,
                    map_text_occurrence_id, detail_code,
                    input_metrics::text, created_at;
                """;

            AddUuid(command, "id", MapQualityFindingId.New().Value);
            AddUuid(command, "quality_run_id", run.Id.Value);
            AddUuid(command, "map_snapshot_id", snapshotId.Value);
            AddText(command, "rule_key", candidate.RuleKey);
            AddText(command, "rule_version", candidate.RuleVersion);
            AddText(
                command,
                "configuration_version",
                candidate.ConfigurationVersion);
            AddText(command, "effect", ToStorage(candidate.Effect));
            AddNullableUuid(
                command,
                "map_item_occurrence_id",
                candidate.MapItemOccurrenceId?.Value);
            AddNullableUuid(
                command,
                "map_text_occurrence_id",
                candidate.MapTextOccurrenceId?.Value);
            AddNullableText(
                command,
                "detail_code",
                candidate.DetailCode);
            AddJsonb(
                command,
                "input_metrics",
                candidate.InputMetricsJson);

            var descriptor = await ReadFindingAsync(
                command,
                cancellationToken)
                ?? throw new CanonicalStateIntegrityException(
                    "Inserted map quality finding was not returned.");
            results.Add(descriptor);
        }

        return results;
    }

    private static async Task<MapObservationDescriptor> InsertObservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MapQualityRunDescriptor run,
        QualityContext context,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO runtime.map_observations
                (id, war_region_id, map_snapshot_id,
                 quality_run_id, validation_fetch_id,
                 quality_decision, capability_kind,
                 observed_at, source_updated_at)
            VALUES
                (@id, @war_region_id, @map_snapshot_id,
                 @quality_run_id, @validation_fetch_id,
                 'accepted', @capability_kind,
                 @observed_at, @source_updated_at)
            RETURNING
                id, war_region_id, map_snapshot_id,
                quality_run_id, validation_fetch_id,
                capability_kind, observed_at,
                source_updated_at, recorded_at;
            """;

        AddUuid(command, "id", MapObservationId.New().Value);
        AddUuid(command, "war_region_id", run.WarRegionId.Value);
        AddUuid(command, "map_snapshot_id", run.MapSnapshotId.Value);
        AddUuid(command, "quality_run_id", run.Id.Value);
        AddUuid(
            command,
            "validation_fetch_id",
            run.ValidationFetchId.Value);
        AddText(
            command,
            "capability_kind",
            ToStorage(context.Snapshot.Kind));
        AddTimestamp(
            command,
            "observed_at",
            context.ValidationRetrievedAt);
        AddNullableTimestamp(
            command,
            "source_updated_at",
            context.Snapshot.SourceUpdatedAt);

        return await ReadObservationAsync(
            command,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "Inserted accepted map observation was not returned.");
    }

    private static async Task<WarRegionDescriptor> EnrichWarRegionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WarRegionDescriptor existing,
        int? sourceRegionId,
        CancellationToken cancellationToken)
    {
        if (sourceRegionId is null ||
            existing.SourceRegionId == sourceRegionId)
        {
            return existing;
        }

        if (existing.SourceRegionId is not null)
        {
            throw new CanonicalStateIntegrityException(
                "Accepted map quality result conflicts with the existing WarRegion source region identifier.");
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
                source_region_id, first_seen_at,
                last_seen_at, created_at;
            """;

        AddUuid(command, "id", existing.Id.Value);
        AddInteger(command, "source_region_id", sourceRegionId.Value);

        return await ReadWarRegionAsync(
            command,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "WarRegion source region enrichment did not update the locked row.");
    }

    private static void ValidateAcceptedSourceRegion(
        WarRegionDescriptor warRegion,
        int? sourceRegionId)
    {
        if (sourceRegionId is < 0)
        {
            throw new CanonicalStateIntegrityException(
                "Accepted map quality cannot enrich a negative source region identifier.");
        }

        if (warRegion.SourceRegionId is { } existing &&
            sourceRegionId is { } supplied &&
            existing != supplied)
        {
            throw new CanonicalStateIntegrityException(
                "Accepted map quality conflicts with the existing WarRegion source region identifier.");
        }
    }

    private static void EnsureDecisionObservationInvariant(
        MapQualityRunDescriptor run,
        MapObservationDescriptor? observation)
    {
        if (run.Decision == MapQualityDecision.Accepted &&
            observation is null)
        {
            throw new CanonicalStateIntegrityException(
                $"Accepted map quality run {run.Id} has no runtime map observation.");
        }

        if (run.Decision != MapQualityDecision.Accepted &&
            observation is not null)
        {
            throw new CanonicalStateIntegrityException(
                $"Non-accepted map quality run {run.Id} unexpectedly has a runtime map observation.");
        }
    }

    private static void EnsureEquivalent(
        MapQualityRunDescriptor existing,
        IReadOnlyList<MapQualityFindingDescriptor> findings,
        MapObservationDescriptor? observation,
        MapQualityWrite supplied,
        QualityContext context)
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
                "Repeated map quality input differs from the durable quality run.");
        }

        if (findings.Count != supplied.Findings.Count)
        {
            throw new CanonicalStateIntegrityException(
                "Repeated map quality input has a different finding count.");
        }

        foreach (var candidate in supplied.Findings)
        {
            if (!findings.Any(
                    durable => FindingEquivalent(
                        durable,
                        candidate)))
            {
                throw new CanonicalStateIntegrityException(
                    "Repeated map quality input contains a finding that differs from durable state.");
            }
        }

        if (observation is not null &&
            (observation.MapSnapshotId != supplied.MapSnapshotId ||
             observation.WarRegionId != supplied.WarRegionId ||
             observation.ValidationFetchId != supplied.ValidationFetchId ||
             observation.Kind != context.Snapshot.Kind ||
             observation.ObservedAt != context.ValidationRetrievedAt ||
             observation.SourceUpdatedAt !=
                 context.Snapshot.SourceUpdatedAt))
        {
            throw new CanonicalStateIntegrityException(
                "Durable accepted map observation differs from its quality provenance.");
        }
    }

    private static bool FindingEquivalent(
        MapQualityFindingDescriptor durable,
        MapQualityFindingCandidate supplied)
    {
        if (!string.Equals(
                durable.RuleKey,
                supplied.RuleKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                durable.RuleVersion,
                supplied.RuleVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                durable.ConfigurationVersion,
                supplied.ConfigurationVersion,
                StringComparison.Ordinal) ||
            durable.Effect != supplied.Effect ||
            durable.MapItemOccurrenceId !=
                supplied.MapItemOccurrenceId ||
            durable.MapTextOccurrenceId !=
                supplied.MapTextOccurrenceId ||
            !string.Equals(
                durable.DetailCode,
                supplied.DetailCode,
                StringComparison.Ordinal))
        {
            return false;
        }

        return JsonbEquals(
            durable.InputMetricsJson,
            supplied.InputMetricsJson);
    }

    private static bool JsonbEquals(
        string left,
        string right)
    {
        using var leftDocument =
            System.Text.Json.JsonDocument.Parse(left);
        using var rightDocument =
            System.Text.Json.JsonDocument.Parse(right);

        return System.Text.Json.JsonElement.DeepEquals(
            leftDocument.RootElement,
            rightDocument.RootElement);
    }

    private static async Task<MapQualityRunDescriptor?>
        GetRunByIdentityAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            MapQualityWrite write,
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

        return await ReadRunAsync(command, cancellationToken);
    }

    private static async Task<MapQualityRunDescriptor?> GetRunByIdAsync(
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
                id, map_snapshot_id, war_region_id,
                validation_fetch_id, taxonomy_version,
                quality_policy_version, baseline_map_observation_id,
                decision, started_at, completed_at, created_at
            FROM quality.map_quality_runs
            WHERE id = @id;
            """;

        AddUuid(command, "id", runId.Value);

        return await ReadRunAsync(command, cancellationToken);
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
                id, quality_run_id, map_snapshot_id,
                rule_key, rule_version, configuration_version,
                effect, map_item_occurrence_id,
                map_text_occurrence_id, detail_code,
                input_metrics::text, created_at
            FROM quality.map_quality_findings
            WHERE quality_run_id = @quality_run_id
            ORDER BY
                rule_key,
                rule_version,
                configuration_version,
                map_item_occurrence_id NULLS FIRST,
                map_text_occurrence_id NULLS FIRST,
                id;
            """;

        AddUuid(command, "quality_run_id", runId.Value);

        var results = new List<MapQualityFindingDescriptor>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                ReadFinding(reader));
        }

        return results;
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
            new MapSnapshotId(reader.GetGuid(2)),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            ParseEffect(reader.GetString(6)),
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
        GetObservationAsync(
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
                capability_kind, observed_at,
                source_updated_at, recorded_at
            FROM runtime.map_observations
            WHERE quality_run_id = @quality_run_id;
            """;

        AddUuid(command, "quality_run_id", runId.Value);

        return await ReadObservationAsync(
            command,
            cancellationToken);
    }

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

    private static async Task<WarRegionDescriptor?> GetWarRegionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        WarRegionId warRegionId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT
                id, war_id, region_id, source_map_name,
                source_region_id, first_seen_at,
                last_seen_at, created_at
            FROM runtime.war_regions
            WHERE id = @id
            {(forUpdate ? "FOR UPDATE" : string.Empty)};
            """;

        AddUuid(command, "id", warRegionId.Value);

        return await ReadWarRegionAsync(
            command,
            cancellationToken);
    }

    private static async Task<WarRegionDescriptor?>
        ReadWarRegionAsync(
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

    private static MapQualityDecision ParseDecision(string value) =>
        value switch
        {
            "accepted" => MapQualityDecision.Accepted,
            "suspect" => MapQualityDecision.Suspect,
            "quarantined" => MapQualityDecision.Quarantined,
            _ => throw new CanonicalStateIntegrityException(
                $"Unknown durable map quality decision '{value}'."),
        };

    private static MapQualityFindingEffect ParseEffect(string value) =>
        value switch
        {
            "informational" => MapQualityFindingEffect.Informational,
            "suspect" => MapQualityFindingEffect.Suspect,
            "quarantined" => MapQualityFindingEffect.Quarantined,
            _ => throw new CanonicalStateIntegrityException(
                $"Unknown durable map quality finding effect '{value}'."),
        };

    private static MapSnapshotKind ParseKind(string value) =>
        value switch
        {
            "static" => MapSnapshotKind.Static,
            "dynamic" => MapSnapshotKind.Dynamic,
            _ => throw new CanonicalStateIntegrityException(
                $"Unknown durable map snapshot kind '{value}'."),
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

    private static string ToStorage(MapQualityFindingEffect value) =>
        value switch
        {
            MapQualityFindingEffect.Informational =>
                "informational",
            MapQualityFindingEffect.Suspect => "suspect",
            MapQualityFindingEffect.Quarantined =>
                "quarantined",
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Unknown map quality finding effect."),
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

    private sealed record QualityContext(
        MapSnapshotDescriptor Snapshot,
        DateTimeOffset ValidationRetrievedAt);

    private static void AddUuid(
        NpgsqlCommand command,
        string name,
        Guid value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value =
            value;

    private static void AddNullableUuid(
        NpgsqlCommand command,
        string name,
        Guid? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value =
            (object?)value ?? DBNull.Value;

    private static void AddText(
        NpgsqlCommand command,
        string name,
        string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value =
            value;

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
        command.Parameters.Add(name, NpgsqlDbType.Integer).Value =
            value;

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
            (object?)value ?? DBNull.Value;

    private static void AddJsonb(
        NpgsqlCommand command,
        string name,
        string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Jsonb).Value =
            value;
}
