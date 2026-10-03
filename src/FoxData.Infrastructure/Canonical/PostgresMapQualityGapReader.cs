using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Canonical;

// M6-G5 scanner for body-bearing 200 quality gaps. Validation-only 304
// bindings remain explicitly owned by G6/G7.
public sealed class PostgresMapQualityGapReader(NpgsqlDataSource dataSource)
    : IMapQualityGapReader
{
    public async Task<IReadOnlyList<MapQualityGapCandidate>> GetPendingAsync(
        string sourceKey,
        IReadOnlyList<CoverageCapabilityPlan> capabilities,
        string taxonomyVersion,
        string qualityPolicyVersion,
        DateTimeOffset? afterObservedAt,
        FetchId? afterValidationFetchId,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ValidateRequiredText(sourceKey, nameof(sourceKey));
        ValidateRequiredText(taxonomyVersion, nameof(taxonomyVersion));
        ValidateRequiredText(
            qualityPolicyVersion,
            nameof(qualityPolicyVersion));
        ValidateCapabilityPlans(capabilities);

        if (batchSize is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        if ((afterObservedAt is null) !=
            (afterValidationFetchId is null))
        {
            throw new ArgumentException(
                "Quality-gap cursor must contain both observation time and validation Fetch ID.");
        }

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH capability_plan AS (
                SELECT *
                FROM unnest(
                    @capability_keys::text[],
                    @parser_versions::text[],
                    @normalizer_versions::text[])
                    AS plan(
                        capability_key,
                        parser_version,
                        normalizer_version)
            )
            SELECT
                snapshot.normalization_run_id,
                endpoint.shard_id,
                endpoint.capability_key,
                representation.id,
                representation.retrieved_at
            FROM evidence.map_snapshots AS snapshot
            JOIN evidence.normalization_runs AS normalization
                ON normalization.id = snapshot.normalization_run_id
               AND normalization.outcome = 'normalized'
            JOIN evidence.source_parse_runs AS source_parse
                ON source_parse.id = snapshot.source_parse_run_id
               AND normalization.source_parse_run_id = source_parse.id
            JOIN evidence.fetches AS representation
                ON representation.id = snapshot.representation_fetch_id
            JOIN ingest.attempts AS attempt
                ON attempt.id = representation.attempt_id
            JOIN sources.endpoints AS endpoint
                ON endpoint.id = representation.endpoint_id
               AND endpoint.capability_key = source_parse.capability_key
            JOIN capability_plan AS plan
                ON plan.capability_key = endpoint.capability_key
               AND plan.parser_version = source_parse.parser_version
               AND plan.normalizer_version =
                   normalization.normalizer_version
            JOIN sources.shards AS shard
                ON shard.id = endpoint.shard_id
            JOIN sources.sources AS source
                ON source.id = shard.source_id
            WHERE source.key = @source_key
              AND representation.status_code = 200
              AND representation.payload_id IS NOT NULL
              AND attempt.outcome_code = 'captured_current'
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM quality.map_quality_runs AS run
                  WHERE run.map_snapshot_id = snapshot.id
                    AND run.validation_fetch_id = representation.id
                    AND run.taxonomy_version = @taxonomy_version
                    AND run.quality_policy_version = @policy_version
              )
              AND (
                  @after_at IS NULL
                  OR (representation.retrieved_at, representation.id)
                        > (@after_at, @after_id)
              )
            ORDER BY representation.retrieved_at, representation.id
            LIMIT @batch_size;
            """;

        command.Parameters.Add("source_key", NpgsqlDbType.Text)
            .Value = sourceKey;
        command.Parameters.Add(
            "capability_keys",
            NpgsqlDbType.Array | NpgsqlDbType.Text)
            .Value = capabilities.Select(x => x.CapabilityKey).ToArray();
        command.Parameters.Add(
            "parser_versions",
            NpgsqlDbType.Array | NpgsqlDbType.Text)
            .Value = capabilities.Select(x => x.ParserVersion).ToArray();
        command.Parameters.Add(
            "normalizer_versions",
            NpgsqlDbType.Array | NpgsqlDbType.Text)
            .Value = capabilities.Select(x => x.NormalizerVersion).ToArray();
        command.Parameters.Add("taxonomy_version", NpgsqlDbType.Text)
            .Value = taxonomyVersion;
        command.Parameters.Add("policy_version", NpgsqlDbType.Text)
            .Value = qualityPolicyVersion;
        command.Parameters.Add("after_at", NpgsqlDbType.TimestampTz)
            .Value = (object?)afterObservedAt ?? DBNull.Value;
        command.Parameters.Add("after_id", NpgsqlDbType.Uuid)
            .Value = (object?)afterValidationFetchId?.Value ?? DBNull.Value;
        command.Parameters.Add("batch_size", NpgsqlDbType.Integer)
            .Value = batchSize;

        var pending = new List<MapQualityGapCandidate>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pending.Add(
                new MapQualityGapCandidate(
                    new NormalizationRunId(reader.GetGuid(0)),
                    new ShardId(reader.GetGuid(1)),
                    reader.GetString(2),
                    new FetchId(reader.GetGuid(3)),
                    reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return pending;
    }

    private static void ValidateCapabilityPlans(
        IReadOnlyList<CoverageCapabilityPlan> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.Count == 0)
        {
            throw new ArgumentException(
                "Quality-gap scanning requires at least one capability plan.",
                nameof(capabilities));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            ValidateRequiredText(
                capability.CapabilityKey,
                nameof(capabilities));
            ValidateRequiredText(
                capability.ParserVersion,
                nameof(capabilities));
            ValidateRequiredText(
                capability.NormalizerVersion,
                nameof(capabilities));

            if (!seen.Add(capability.CapabilityKey))
            {
                throw new ArgumentException(
                    $"Duplicate quality-gap capability '{capability.CapabilityKey}'.",
                    nameof(capabilities));
            }
        }
    }

    private static void ValidateRequiredText(
        string value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Quality-gap identities must be non-empty and already trimmed.",
                parameterName);
        }
    }
}
