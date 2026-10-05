using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Canonical;

// M6-G quality-gap scanner over selected parser/normalizer identities.
// G6/G7 add exact-lineage 304 validation bindings while the Worker remains
// responsible for proving the authoritative WarRegion at each validation
// boundary before evaluation.
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
            ),
            pending AS (
                SELECT
                    snapshot.id AS map_snapshot_id,
                    snapshot.normalization_run_id,
                    endpoint.shard_id,
                    endpoint.capability_key,
                    CASE
                        WHEN validation.id = representation.id
                            THEN 'body_200'
                        ELSE 'not_modified_304'
                    END AS validation_kind,
                    representation.retrieved_at
                        AS representation_observed_at,
                    validation.id AS validation_fetch_id,
                    validation.retrieved_at AS observed_at
                FROM evidence.map_snapshots AS snapshot
                JOIN evidence.normalization_runs AS normalization
                    ON normalization.id = snapshot.normalization_run_id
                   AND normalization.outcome = 'normalized'
                JOIN evidence.source_parse_runs AS source_parse
                    ON source_parse.id = snapshot.source_parse_run_id
                   AND normalization.source_parse_run_id = source_parse.id
                JOIN evidence.fetches AS representation
                    ON representation.id =
                       snapshot.representation_fetch_id
                JOIN ingest.attempts AS representation_attempt
                    ON representation_attempt.id =
                       representation.attempt_id
                JOIN sources.endpoints AS endpoint
                    ON endpoint.id = representation.endpoint_id
                   AND endpoint.capability_key =
                       source_parse.capability_key
                JOIN capability_plan AS plan
                    ON plan.capability_key = endpoint.capability_key
                   AND plan.parser_version =
                       source_parse.parser_version
                   AND plan.normalizer_version =
                       normalization.normalizer_version
                JOIN sources.shards AS shard
                    ON shard.id = endpoint.shard_id
                JOIN sources.sources AS source
                    ON source.id = shard.source_id
                JOIN evidence.fetches AS validation
                    ON validation.endpoint_id = representation.endpoint_id
                   AND (
                        (
                            validation.id = representation.id
                            AND validation.status_code = 200
                            AND validation.payload_id IS NOT NULL
                        )
                        OR
                        (
                            validation.status_code = 304
                            AND validation.payload_id IS NULL
                            AND validation.prior_fetch_id =
                                representation.id
                        )
                   )
                JOIN ingest.attempts AS validation_attempt
                    ON validation_attempt.id = validation.attempt_id
                WHERE source.key = @source_key
                  AND representation.status_code = 200
                  AND representation.payload_id IS NOT NULL
                  AND representation_attempt.outcome_code =
                      'captured_current'
                  AND validation_attempt.outcome_code =
                      'captured_current'
                  AND (
                        validation.id = representation.id
                        OR
                        (validation.retrieved_at, validation.id)
                            > (representation.retrieved_at,
                               representation.id)
                      )
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM quality.map_quality_runs AS run
                      WHERE run.map_snapshot_id = snapshot.id
                        AND run.validation_fetch_id = validation.id
                        AND run.taxonomy_version = @taxonomy_version
                        AND run.quality_policy_version =
                            @policy_version
                  )
            )
            SELECT
                normalization_run_id,
                shard_id,
                capability_key,
                validation_kind,
                representation_observed_at,
                validation_fetch_id,
                observed_at
            FROM pending
            WHERE (
                @after_at IS NULL
                OR (observed_at, validation_fetch_id)
                    > (@after_at, @after_id)
            )
            ORDER BY observed_at, validation_fetch_id
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
                    reader.GetString(3) switch
                    {
                        "body_200" =>
                            MapQualityGapValidationKind.BodyBearing200,
                        "not_modified_304" =>
                            MapQualityGapValidationKind.NotModified304,
                        var value =>
                            throw new CanonicalStateIntegrityException(
                                $"Unknown quality-gap validation kind '{value}'."),
                    },
                    reader.GetFieldValue<DateTimeOffset>(4),
                    new FetchId(reader.GetGuid(5)),
                    reader.GetFieldValue<DateTimeOffset>(6)));
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
