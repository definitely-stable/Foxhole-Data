using System.Data;
using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Canonical;

public sealed class PostgresWarContextReader(NpgsqlDataSource dataSource)
    : IWarContextReader
{
    public async Task<WarSourceContextDescriptor?> GetAtOrBeforeAsync(
        ShardId shardId,
        DateTimeOffset observedAt,
        string capabilityKey,
        string semanticKey,
        string parserVersion,
        CancellationToken cancellationToken)
    {
        if (shardId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Shard identifier must not be empty.",
                nameof(shardId));
        }

        ValidateRequiredText(capabilityKey, 128, nameof(capabilityKey));
        ValidateRequiredText(semanticKey, 256, nameof(semanticKey));
        ValidateRequiredText(parserVersion, 128, nameof(parserVersion));

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH candidate AS
            (
                SELECT
                    source_fetch.id AS validation_fetch_id,
                    CASE
                        WHEN source_fetch.payload_id IS NOT NULL
                            THEN source_fetch.id
                        ELSE source_fetch.prior_fetch_id
                    END AS representation_fetch_id,
                    source_fetch.retrieved_at,
                    source_fetch.status_code
                FROM evidence.fetches AS source_fetch
                INNER JOIN ingest.attempts AS attempt
                    ON attempt.id = source_fetch.attempt_id
                INNER JOIN sources.endpoints AS endpoint
                    ON endpoint.id = source_fetch.endpoint_id
                WHERE endpoint.shard_id = @shard_id
                  AND attempt.outcome_code = 'captured_current'
                  AND endpoint.capability_key = @capability_key
                  AND endpoint.semantic_key = @semantic_key
                  AND source_fetch.retrieved_at <= @observed_at
                ORDER BY
                    source_fetch.retrieved_at DESC,
                    source_fetch.id DESC
                LIMIT 1
            )
            SELECT
                candidate.validation_fetch_id,
                candidate.representation_fetch_id,
                candidate.retrieved_at,
                candidate.status_code,
                parse_run.id
            FROM candidate
            LEFT JOIN evidence.source_parse_runs AS parse_run
                ON parse_run.representation_fetch_id = candidate.representation_fetch_id
               AND parse_run.capability_key = @capability_key
               AND parse_run.parser_version = @parser_version;
            """;

        AddUuid(command, "shard_id", shardId.Value);
        AddText(command, "capability_key", capabilityKey);
        AddText(command, "semantic_key", semanticKey);
        AddTimestamp(command, "observed_at", observedAt);
        AddText(command, "parser_version", parserVersion);

        await using var result = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await result.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WarSourceContextDescriptor(
            new FetchId(result.GetGuid(0)),
            result.IsDBNull(1)
                ? null
                : new FetchId(result.GetGuid(1)),
            result.IsDBNull(4)
                ? null
                : new SourceParseRunId(result.GetGuid(4)),
            result.GetFieldValue<DateTimeOffset>(2),
            result.IsDBNull(3)
                ? null
                : result.GetInt32(3));
    }

    private static void ValidateRequiredText(
        string value,
        int maximum,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximum ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{parameterName} must be non-empty, already trimmed, and at most {maximum} characters.",
                parameterName);
        }
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
}
