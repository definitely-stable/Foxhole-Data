using System.Data;
using FoxData.Sources.WarApi;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.RecoveryTests;

// Independent M6-H audit. Unlike the recovery gap reader, this starts from
// authoritative source Attempts/Fetches rather than existing MapSnapshots.
// It never inserts, repairs or changes source evidence.
internal static class M6CompletionChronologyAudit
{
    private const string Sql =
        """
        WITH terminal_attempts AS
        (
            SELECT a.id AS attempt_id, e.id AS endpoint_id
            FROM ingest.attempts AS a
            JOIN ingest.collection_jobs AS job ON job.id = a.job_id
            JOIN sources.endpoints AS e ON e.id = job.endpoint_id
            JOIN sources.shards AS shard ON shard.id = e.shard_id
            JOIN sources.sources AS source ON source.id = shard.source_id
            WHERE source.key = @source_key
              AND e.capability_key IN ('static-map-state', 'dynamic-map-state')
              AND a.state IN
                  ('completed', 'failed', 'uncertain', 'superseded', 'captured_late')
        ),
        missing_coverage AS
        (
            SELECT a.attempt_id
            FROM terminal_attempts AS a
            LEFT JOIN evidence.coverage_observations AS coverage
                ON coverage.attempt_id = a.attempt_id
            WHERE coverage.id IS NULL
        ),
        validation_source AS
        (
            SELECT
                v.id AS validation_id,
                v.retrieved_at AS observed_at,
                v.endpoint_id,
                v.status_code,
                v.payload_id,
                v.prior_fetch_id,
                v.body_error_code,
                CASE WHEN v.status_code = 200
                    THEN v.id
                    ELSE v.prior_fetch_id END AS representation_id,
                e.capability_key,
                a.id AS attempt_id
            FROM evidence.fetches AS v
            JOIN ingest.attempts AS a ON a.id = v.attempt_id
            JOIN sources.endpoints AS e ON e.id = v.endpoint_id
            JOIN sources.shards AS shard ON shard.id = e.shard_id
            JOIN sources.sources AS source ON source.id = shard.source_id
            WHERE source.key = @source_key
              AND a.outcome_code = 'captured_current'
              AND e.capability_key IN ('static-map-state', 'dynamic-map-state')
              AND v.status_code IN (200, 304)
        ),
        validation_graph AS
        (
            SELECT
                v.*,
                r.retrieved_at AS representation_at,
                r.endpoint_id AS representation_endpoint_id,
                r.status_code AS representation_status,
                r.payload_id AS representation_payload_id,
                ra.outcome_code AS representation_outcome,
                parsed.id AS parse_id,
                parsed.outcome AS parse_outcome,
                normalized.id AS normalization_id,
                normalized.outcome AS normalization_outcome,
                snapshot.id AS snapshot_id,
                quality.id AS quality_id,
                coverage.id AS coverage_id,
                coverage.state AS coverage_state,
                coverage.endpoint_id AS coverage_endpoint_id,
                coverage.validation_fetch_id AS coverage_validation_id,
                coverage.representation_fetch_id AS coverage_representation_id,
                coverage.source_parse_run_id AS coverage_parse_id
            FROM validation_source AS v
            LEFT JOIN evidence.fetches AS r
                ON r.id = v.representation_id
            LEFT JOIN ingest.attempts AS ra ON ra.id = r.attempt_id
            LEFT JOIN evidence.source_parse_runs AS parsed
                ON parsed.representation_fetch_id = r.id
               AND parsed.capability_key = v.capability_key
               AND parsed.parser_version = @parser_version
            LEFT JOIN evidence.normalization_runs AS normalized
                ON normalized.source_parse_run_id = parsed.id
               AND normalized.normalizer_version = CASE v.capability_key
                   WHEN 'static-map-state' THEN @static_normalizer
                   WHEN 'dynamic-map-state' THEN @dynamic_normalizer
                   END
            LEFT JOIN evidence.map_snapshots AS snapshot
                ON snapshot.normalization_run_id = normalized.id
            LEFT JOIN quality.map_quality_runs AS quality
                ON quality.map_snapshot_id = snapshot.id
               AND quality.validation_fetch_id = v.validation_id
               AND quality.taxonomy_version = @taxonomy_version
               AND quality.quality_policy_version = @policy_version
            LEFT JOIN evidence.coverage_observations AS coverage
                ON coverage.attempt_id = v.attempt_id
        ),
        invalid_lineage AS
        (
            SELECT validation_id
            FROM validation_graph
            WHERE body_error_code IS NOT NULL
               OR (status_code = 200 AND payload_id IS NULL)
               OR (status_code = 304 AND payload_id IS NOT NULL)
               OR representation_id IS NULL
               OR representation_status IS DISTINCT FROM 200
               OR representation_payload_id IS NULL
               OR representation_endpoint_id IS DISTINCT FROM endpoint_id
               OR representation_outcome IS DISTINCT FROM 'captured_current'
               OR (status_code = 304 AND
                   (representation_id = validation_id OR
                    (observed_at, validation_id) <=
                        (representation_at, representation_id)))
        ),
        invalid_validation AS
        (
            SELECT bad.validation_id
            FROM invalid_lineage AS bad
            JOIN validation_graph AS v
                ON v.validation_id = bad.validation_id
            WHERE v.coverage_id IS NULL
               OR v.coverage_state IN ('observed', 'source_not_modified')
        ),
        eligible AS
        (
            SELECT *
            FROM validation_graph AS v
            WHERE NOT EXISTS
            (
                SELECT 1 FROM invalid_lineage AS bad
                WHERE bad.validation_id = v.validation_id
            )
        ),
        missing_parse AS
        (
            SELECT validation_id
            FROM eligible
            WHERE parse_id IS NULL
        ),
        missing_normalization AS
        (
            SELECT validation_id
            FROM eligible
            WHERE parse_outcome IN ('parsed', 'parsed_with_unknowns')
              AND normalization_id IS NULL
        ),
        missing_snapshot AS
        (
            SELECT validation_id
            FROM eligible
            WHERE normalization_outcome = 'normalized'
              AND snapshot_id IS NULL
        ),
        missing_quality AS
        (
            SELECT validation_id
            FROM eligible
            WHERE normalization_outcome = 'normalized'
              AND snapshot_id IS NOT NULL
              AND quality_id IS NULL
        ),
        coverage_binding AS
        (
            SELECT validation_id
            FROM eligible
            WHERE parse_outcome IN ('parsed', 'parsed_with_unknowns')
              AND
              (
                  coverage_id IS NULL
                  OR coverage_endpoint_id IS DISTINCT FROM endpoint_id
                  OR coverage_validation_id IS DISTINCT FROM validation_id
                  OR coverage_representation_id IS DISTINCT FROM representation_id
                  OR coverage_parse_id IS DISTINCT FROM parse_id
                  OR coverage_state IS DISTINCT FROM CASE status_code
                      WHEN 200 THEN 'observed'
                      WHEN 304 THEN 'source_not_modified'
                      END
              )
        ),
        duplicate_quality AS
        (
            SELECT validation_id
            FROM eligible
            GROUP BY validation_id
            HAVING count(quality_id) > 1
        ),
        later_terminal_over_gap AS
        (
            SELECT DISTINCT later.id
            FROM quality.map_quality_runs AS later
            JOIN evidence.map_snapshots AS later_snapshot
                ON later_snapshot.id = later.map_snapshot_id
            JOIN evidence.fetches AS later_validation
                ON later_validation.id = later.validation_fetch_id
            JOIN evidence.fetches AS later_representation
                ON later_representation.id =
                    later_snapshot.representation_fetch_id
            JOIN runtime.war_regions AS region
                ON region.id = later.war_region_id
            WHERE later.taxonomy_version = @taxonomy_version
              AND later.quality_policy_version = @policy_version
              AND EXISTS
              (
                  SELECT 1
                  FROM eligible AS earlier
                  WHERE earlier.endpoint_id = later_representation.endpoint_id
                    AND earlier.observed_at >= region.first_seen_at
                    AND (earlier.observed_at, earlier.validation_id)
                        < (later_validation.retrieved_at, later_validation.id)
                    AND
                    (
                        earlier.parse_id IS NULL
                        OR
                        (
                            earlier.parse_outcome IN
                                ('parsed', 'parsed_with_unknowns')
                            AND
                            (
                                earlier.normalization_id IS NULL
                                OR
                                (
                                    earlier.normalization_outcome = 'normalized'
                                    AND NOT EXISTS
                                    (
                                        SELECT 1
                                        FROM quality.map_quality_runs AS prior
                                        WHERE prior.map_snapshot_id =
                                            earlier.snapshot_id
                                          AND prior.validation_fetch_id =
                                            earlier.validation_id
                                          AND prior.war_region_id =
                                            later.war_region_id
                                          AND prior.taxonomy_version =
                                            later.taxonomy_version
                                          AND prior.quality_policy_version =
                                            later.quality_policy_version
                                    )
                                )
                            )
                        )
                    )
              )
        )
        SELECT 'missing_coverage' AS code, count(*) AS violations
            FROM missing_coverage
        UNION ALL
        SELECT 'invalid_validation', count(*) FROM invalid_validation
        UNION ALL
        SELECT 'missing_parse', count(*) FROM missing_parse
        UNION ALL
        SELECT 'missing_normalization', count(*) FROM missing_normalization
        UNION ALL
        SELECT 'missing_snapshot', count(*) FROM missing_snapshot
        UNION ALL
        SELECT 'missing_quality', count(*) FROM missing_quality
        UNION ALL
        SELECT 'coverage_binding', count(*) FROM coverage_binding
        UNION ALL
        SELECT 'duplicate_quality', count(*) FROM duplicate_quality
        UNION ALL
        SELECT 'later_terminal_over_gap', count(*)
            FROM later_terminal_over_gap
        ORDER BY code;
        """;

    public static async Task<IReadOnlyDictionary<string, long>> ReadAsync(
        NpgsqlDataSource dataSource,
        string policyVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        command.Parameters.Add("source_key", NpgsqlDbType.Text).Value =
            WarApiCatalog.SourceKey;
        command.Parameters.Add("parser_version", NpgsqlDbType.Text).Value =
            WarApiVersions.Parser;
        command.Parameters.Add("static_normalizer", NpgsqlDbType.Text).Value =
            WarApiVersions.StaticMapNormalizer;
        command.Parameters.Add("dynamic_normalizer", NpgsqlDbType.Text).Value =
            WarApiVersions.DynamicMapNormalizer;
        command.Parameters.Add("taxonomy_version", NpgsqlDbType.Text).Value =
            WarApiVersions.MapTaxonomy;
        command.Parameters.Add("policy_version", NpgsqlDbType.Text).Value =
            policyVersion;

        var result = new SortedDictionary<string, long>(
            StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(
                         cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!result.TryAdd(reader.GetString(0), reader.GetInt64(1)))
                {
                    throw new InvalidOperationException(
                        "Duplicate M6-H chronology audit category.");
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
        if (result.Count != 9)
        {
            throw new InvalidOperationException(
                "M6-H chronology audit did not return all expected categories.");
        }

        return result;
    }

    public static async Task AssertCleanAsync(
        NpgsqlDataSource dataSource,
        string policyVersion,
        CancellationToken cancellationToken)
    {
        var result = await ReadAsync(
            dataSource, policyVersion, cancellationToken);
        var failures = result.Where(x => x.Value != 0).ToArray();
        if (failures.Length > 0)
        {
            throw new InvalidOperationException(
                "M6-H source-first chronology audit failed: " +
                string.Join(", ", failures.Select(
                    x => $"{x.Key}={x.Value}")));
        }
    }
}
