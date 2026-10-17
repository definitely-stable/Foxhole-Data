using System.Data;
using Npgsql;

namespace FoxData.RecoveryTests;

// M6-H proof reader, intentionally independent of the quality/recovery writers.
// Execute on one repeatable-read snapshot; otherwise concurrent commits can
// make two individually correct queries appear mutually inconsistent.
internal static class M6CompletionProvenanceAudit
{
    private const string Sql =
        """
        WITH
        snapshot_lineage AS
        (
            SELECT s.id
            FROM evidence.map_snapshots AS s
            JOIN evidence.normalization_runs AS n
                ON n.id = s.normalization_run_id
            JOIN evidence.source_parse_runs AS p
                ON p.id = s.source_parse_run_id
            JOIN evidence.fetches AS f
                ON f.id = s.representation_fetch_id
            JOIN ingest.attempts AS a
                ON a.id = f.attempt_id
            JOIN sources.endpoints AS e
                ON e.id = f.endpoint_id
            WHERE n.source_parse_run_id <> s.source_parse_run_id
               OR n.outcome <> 'normalized'
               OR n.normalizer_version <> CASE s.capability_kind
                   WHEN 'static' THEN 'warapi-static-map-normalizer@1'
                   WHEN 'dynamic' THEN 'warapi-dynamic-map-normalizer@1'
                   END
               OR p.representation_fetch_id <> s.representation_fetch_id
               OR p.capability_key <> CASE s.capability_kind
                   WHEN 'static' THEN 'static-map-state'
                   WHEN 'dynamic' THEN 'dynamic-map-state'
                   END
               OR p.outcome NOT IN ('parsed', 'parsed_with_unknowns')
               OR e.capability_key <> p.capability_key
               OR f.status_code <> 200
               OR f.payload_id IS NULL
               OR a.outcome_code <> 'captured_current'
        ),
        item_ordinal AS
        (
            SELECT map_snapshot_id, count(*) AS total,
                   min(source_ordinal) AS first_ordinal,
                   max(source_ordinal) AS last_ordinal
            FROM evidence.map_item_occurrences
            GROUP BY map_snapshot_id
        ),
        text_ordinal AS
        (
            SELECT map_snapshot_id, count(*) AS total,
                   min(source_ordinal) AS first_ordinal,
                   max(source_ordinal) AS last_ordinal
            FROM evidence.map_text_occurrences
            GROUP BY map_snapshot_id
        ),
        snapshot_occurrences AS
        (
            SELECT s.id
            FROM evidence.map_snapshots AS s
            LEFT JOIN item_ordinal AS i ON i.map_snapshot_id = s.id
            LEFT JOIN text_ordinal AS t ON t.map_snapshot_id = s.id
            WHERE coalesce(i.total, 0) <> s.item_count
               OR coalesce(t.total, 0) <> s.text_item_count
               OR (s.item_count > 0 AND
                   (i.first_ordinal <> 0 OR
                    i.last_ordinal <> s.item_count - 1))
               OR (s.text_item_count > 0 AND
                   (t.first_ordinal <> 0 OR
                    t.last_ordinal <> s.text_item_count - 1))
        ),
        quality_lineage AS
        (
            SELECT q.id
            FROM quality.map_quality_runs AS q
            JOIN evidence.map_snapshots AS s ON s.id = q.map_snapshot_id
            JOIN evidence.fetches AS repr
                ON repr.id = s.representation_fetch_id
            JOIN evidence.fetches AS v ON v.id = q.validation_fetch_id
            JOIN ingest.attempts AS a ON a.id = v.attempt_id
            JOIN sources.endpoints AS ep ON ep.id = repr.endpoint_id
            JOIN runtime.war_regions AS wr ON wr.id = q.war_region_id
            JOIN runtime.wars AS w ON w.id = wr.war_id
            WHERE wr.source_map_name <> s.source_map_name
               OR ep.shard_id <> w.shard_id
               OR ep.id <> v.endpoint_id
               OR a.outcome_code <> 'captured_current'
               OR v.retrieved_at < wr.first_seen_at
               OR NOT
               (
                   (v.id = repr.id
                    AND v.status_code = 200
                    AND v.payload_id IS NOT NULL)
                   OR
                   (v.id <> repr.id
                    AND v.status_code = 304
                    AND v.payload_id IS NULL
                    AND v.prior_fetch_id = repr.id
                    AND (v.retrieved_at, v.id) >
                        (repr.retrieved_at, repr.id))
               )
        ),
        observation_binding AS
        (
            SELECT q.id
            FROM quality.map_quality_runs AS q
            JOIN evidence.map_snapshots AS s ON s.id = q.map_snapshot_id
            JOIN evidence.fetches AS v ON v.id = q.validation_fetch_id
            LEFT JOIN runtime.map_observations AS o
                ON o.quality_run_id = q.id
            WHERE (q.decision = 'accepted'
                    AND (o.id IS NULL
                         OR o.observed_at IS DISTINCT FROM v.retrieved_at
                         OR o.source_updated_at IS DISTINCT FROM
                            s.source_updated_at
                         OR o.capability_kind <> s.capability_kind))
               OR (q.decision <> 'accepted' AND o.id IS NOT NULL)
        ),
        baseline_binding AS
        (
            SELECT q.id
            FROM quality.map_quality_runs AS q
            JOIN evidence.map_snapshots AS s ON s.id = q.map_snapshot_id
            JOIN evidence.fetches AS v ON v.id = q.validation_fetch_id
            LEFT JOIN LATERAL
            (
                SELECT o.id
                FROM runtime.map_observations AS o
                JOIN quality.map_quality_runs AS prior_q
                    ON prior_q.id = o.quality_run_id
                WHERE o.war_region_id = q.war_region_id
                  AND o.capability_kind = s.capability_kind
                  AND prior_q.taxonomy_version = q.taxonomy_version
                  AND prior_q.quality_policy_version =
                      q.quality_policy_version
                  AND prior_q.decision = 'accepted'
                  AND (o.observed_at, o.validation_fetch_id)
                      < (v.retrieved_at, q.validation_fetch_id)
                ORDER BY o.observed_at DESC,
                         o.validation_fetch_id DESC
                LIMIT 1
            ) AS expected ON true
            WHERE q.baseline_map_observation_id IS DISTINCT FROM expected.id
        ),
        finding_effects AS
        (
            SELECT f.id
            FROM quality.map_quality_findings AS f
            JOIN quality.map_quality_runs AS q ON q.id = f.quality_run_id
            WHERE (q.decision = 'accepted'
                   AND f.effect IN ('suspect', 'quarantined'))
               OR (q.decision = 'suspect' AND f.effect = 'quarantined')
               OR f.map_snapshot_id <> q.map_snapshot_id
        ),
        region_enrichment AS
        (
            SELECT DISTINCT q.war_region_id
            FROM quality.map_quality_runs AS q
            JOIN evidence.map_snapshots AS s ON s.id = q.map_snapshot_id
            JOIN runtime.war_regions AS wr ON wr.id = q.war_region_id
            WHERE q.decision = 'accepted'
              AND s.source_region_id IS NOT NULL
              AND wr.source_region_id IS DISTINCT FROM s.source_region_id
        )
        SELECT 'snapshot_lineage' AS code, count(*) AS violations
            FROM snapshot_lineage
        UNION ALL
        SELECT 'snapshot_occurrences', count(*) FROM snapshot_occurrences
        UNION ALL
        SELECT 'quality_lineage', count(*) FROM quality_lineage
        UNION ALL
        SELECT 'observation_binding', count(*) FROM observation_binding
        UNION ALL
        SELECT 'baseline_binding', count(*) FROM baseline_binding
        UNION ALL
        SELECT 'finding_effects', count(*) FROM finding_effects
        UNION ALL
        SELECT 'region_enrichment', count(*) FROM region_enrichment
        ORDER BY code;
        """;

    public static async Task<IReadOnlyDictionary<string, long>> ReadAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;

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
                        "Duplicate M6-H audit category.");
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
        if (result.Count != 7)
        {
            throw new InvalidOperationException(
                "M6-H provenance audit did not return all expected categories.");
        }

        return result;
    }

    public static async Task AssertCleanAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        var result = await ReadAsync(dataSource, cancellationToken);
        var problems = result.Where(x => x.Value != 0).ToArray();
        if (problems.Length > 0)
        {
            throw new InvalidOperationException(
                "M6-H independent provenance audit failed: " +
                string.Join(", ", problems.Select(
                    x => $"{x.Key}={x.Value}")));
        }
    }
}
