using System.Data;
using FoxData.Sources.WarApi;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.RecoveryTests;

// M6-H5/H8 semantic oracle. Read only from persisted SQL rows: never reuse
// the producer's DTO serializer, kernel equivalence, or generated local IDs.
// Each relation is projected onto durable source/region/version identities.
// PostgreSQL jsonb normalizes metrics-object key order before comparison.
internal static class M6SemanticRebuildProof
{
    private static readonly (string Name, string Sql)[] SemanticQueries =
    [
        ("snapshots",
            """
            SELECT coalesce(jsonb_agg(jsonb_build_array(
                s.representation_fetch_id, s.source_parse_run_id,
                n.normalizer_version, s.capability_kind, s.source_map_name,
                s.source_region_id, s.source_scorched_victory_towns,
                s.source_version, s.source_last_updated_ms,
                s.source_updated_at,
                s.source_map_items_array_present,
                s.source_map_text_items_array_present,
                s.item_count, s.text_item_count
            ) ORDER BY s.representation_fetch_id), '[]'::jsonb)::text
            FROM evidence.map_snapshots AS s
            JOIN evidence.normalization_runs AS n
                ON n.id = s.normalization_run_id;
            """),
        ("items",
            """
            SELECT coalesce(jsonb_agg(jsonb_build_array(
                s.representation_fetch_id,
                item.source_ordinal, item.raw_team_id, item.raw_icon_type,
                item.x, item.y, item.raw_flags, item.raw_view_direction
            ) ORDER BY s.representation_fetch_id, item.source_ordinal),
                '[]'::jsonb)::text
            FROM evidence.map_item_occurrences AS item
            JOIN evidence.map_snapshots AS s ON s.id = item.map_snapshot_id;
            """),
        ("text_items",
            """
            SELECT coalesce(jsonb_agg(jsonb_build_array(
                s.representation_fetch_id, item.source_ordinal, item.text,
                item.x, item.y, item.raw_map_marker_type
            ) ORDER BY s.representation_fetch_id, item.source_ordinal),
                '[]'::jsonb)::text
            FROM evidence.map_text_occurrences AS item
            JOIN evidence.map_snapshots AS s ON s.id = item.map_snapshot_id;
            """),
        ("quality_runs",
            """
            SELECT coalesce(jsonb_agg(jsonb_build_array(
                q.war_region_id, s.representation_fetch_id,
                q.validation_fetch_id, q.taxonomy_version,
                q.quality_policy_version, q.decision,
                baseline.war_region_id, baseline.validation_fetch_id
            ) ORDER BY q.validation_fetch_id, q.war_region_id,
                       q.quality_policy_version, q.taxonomy_version),
                '[]'::jsonb)::text
            FROM quality.map_quality_runs AS q
            JOIN evidence.map_snapshots AS s ON s.id = q.map_snapshot_id
            LEFT JOIN runtime.map_observations AS baseline
                ON baseline.id = q.baseline_map_observation_id;
            """),
        ("quality_findings",
            """
            SELECT coalesce(jsonb_agg(jsonb_build_array(
                q.war_region_id, q.validation_fetch_id,
                q.taxonomy_version, q.quality_policy_version,
                f.rule_key, f.rule_version, f.configuration_version,
                f.effect, item.source_ordinal, text_item.source_ordinal,
                f.detail_code, f.input_metrics
            ) ORDER BY q.validation_fetch_id, q.war_region_id,
                       q.quality_policy_version, q.taxonomy_version,
                       f.rule_key, f.rule_version, f.configuration_version,
                       f.effect, item.source_ordinal NULLS FIRST,
                       text_item.source_ordinal NULLS FIRST,
                       f.detail_code NULLS FIRST, f.input_metrics::text),
                '[]'::jsonb)::text
            FROM quality.map_quality_findings AS f
            JOIN quality.map_quality_runs AS q ON q.id = f.quality_run_id
            LEFT JOIN evidence.map_item_occurrences AS item
                ON item.id = f.map_item_occurrence_id
            LEFT JOIN evidence.map_text_occurrences AS text_item
                ON text_item.id = f.map_text_occurrence_id;
            """),
        ("observations",
            """
            SELECT coalesce(jsonb_agg(jsonb_build_array(
                o.war_region_id, o.validation_fetch_id,
                q.taxonomy_version, q.quality_policy_version,
                s.representation_fetch_id, o.capability_kind,
                o.observed_at, o.source_updated_at
            ) ORDER BY o.validation_fetch_id, o.war_region_id,
                       q.quality_policy_version, q.taxonomy_version),
                '[]'::jsonb)::text
            FROM runtime.map_observations AS o
            JOIN quality.map_quality_runs AS q ON q.id = o.quality_run_id
            JOIN evidence.map_snapshots AS s ON s.id = o.map_snapshot_id;
            """),
    ];

    private const string AuthoritySql =
        """
        SELECT jsonb_build_array(
            (SELECT coalesce(jsonb_agg(id ORDER BY id), '[]'::jsonb)
             FROM evidence.fetches),
            (SELECT coalesce(jsonb_agg(
                 jsonb_build_array(id, sha256, byte_length)
                 ORDER BY id), '[]'::jsonb) FROM evidence.payloads),
            (SELECT coalesce(jsonb_agg(
                 jsonb_build_array(id, representation_fetch_id,
                     capability_key, parser_version, outcome,
                     structural_fingerprint)
                 ORDER BY id), '[]'::jsonb)
             FROM evidence.source_parse_runs),
            (SELECT coalesce(jsonb_agg(id ORDER BY id), '[]'::jsonb)
             FROM evidence.coverage_observations),
            (SELECT coalesce(jsonb_agg(
                 jsonb_build_array(id, war_id, region_id, source_map_name,
                     source_region_id, first_seen_at, last_seen_at)
                 ORDER BY id), '[]'::jsonb)
             FROM runtime.war_regions),
            (SELECT coalesce(jsonb_agg(id ORDER BY id), '[]'::jsonb)
             FROM evidence.coverage_reprocessing_runs)
        )::text;
        """;

    public static async Task<IReadOnlyDictionary<string, string>> ReadAsync(
        NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        var result = new SortedDictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var (name, sql) in SemanticQueries)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            result.Add(name, (string)(await command.ExecuteScalarAsync(
                cancellationToken))!);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public static async Task<string> ReadAuthorityAsync(
        NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = AuthoritySql;
        return (string)(await command.ExecuteScalarAsync(
            cancellationToken))!;
    }

    // Only call inside an isolated RecoveryPostgresFixture that owns its data.
    // Explicit DELETE (no TRUNCATE, no CASCADE) preserves M2/M3/M5 evidence,
    // WarRegions, coverage, raw Fetch/Payload, parse runs, and continuities.
    // The deletes run atomically or do not run at all.
    public static async Task DeleteDerivedAsync(
        NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM quality.map_quality_findings;
            DELETE FROM runtime.map_observations;
            DELETE FROM quality.map_quality_runs;
            DELETE FROM evidence.map_item_occurrences;
            DELETE FROM evidence.map_text_occurrences;
            DELETE FROM evidence.map_snapshots;
            DELETE FROM evidence.normalization_runs
            WHERE normalizer_version IN (@static_normalizer,
                                         @dynamic_normalizer);
            """;
        command.Parameters.Add("static_normalizer", NpgsqlDbType.Text)
            .Value = WarApiVersions.StaticMapNormalizer;
        command.Parameters.Add("dynamic_normalizer", NpgsqlDbType.Text)
            .Value = WarApiVersions.DynamicMapNormalizer;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public static void AssertEquivalent(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
    {
        if (expected.Count != SemanticQueries.Length ||
            actual.Count != SemanticQueries.Length)
        {
            throw new InvalidOperationException(
                "M6-H semantic snapshots have incomplete relation coverage.");
        }

        foreach (var (name, _) in SemanticQueries)
        {
            if (!expected.TryGetValue(name, out var left) ||
                !actual.TryGetValue(name, out var right) ||
                !string.Equals(left, right, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"M6-H semantic rebuild differs in '{name}'.");
            }
        }
    }
}
