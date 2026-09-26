using System.Data;
using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Canonical;

public sealed class PostgresCanonicalEvidenceReader(NpgsqlDataSource dataSource)
    : ICanonicalEvidenceReader
{
    public async Task<CanonicalEvidenceInput?> GetAsync(
        SourceParseRunId sourceParseRunId,
        CancellationToken cancellationToken)
    {
        if (sourceParseRunId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Source parse run identifier must not be empty.",
                nameof(sourceParseRunId));
        }

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                parse_run.id,
                parse_run.representation_fetch_id,
                fetch.payload_id,
                source.id,
                shard.id,
                endpoint.id,
                source.key,
                shard.environment,
                shard.key,
                endpoint.capability_key,
                endpoint.semantic_key,
                parse_run.adapter_version,
                parse_run.parser_version,
                parse_run.outcome,
                fetch.content_encoding,
                fetch.retrieved_at,
                payload.body
            FROM evidence.source_parse_runs AS parse_run
            INNER JOIN evidence.fetches AS fetch
                ON fetch.id = parse_run.representation_fetch_id
            INNER JOIN evidence.payloads AS payload
                ON payload.id = fetch.payload_id
            INNER JOIN sources.endpoints AS endpoint
                ON endpoint.id = fetch.endpoint_id
            INNER JOIN sources.shards AS shard
                ON shard.id = endpoint.shard_id
            INNER JOIN sources.sources AS source
                ON source.id = shard.source_id
            WHERE parse_run.id = @source_parse_run_id;
            """;

        command.Parameters.Add("source_parse_run_id", NpgsqlDbType.Uuid).Value =
            sourceParseRunId.Value;

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (reader.IsDBNull(2))
        {
            throw new CanonicalStateIntegrityException(
                $"Source parse run {sourceParseRunId} does not reference a body-bearing Fetch.");
        }

        return new CanonicalEvidenceInput(
            new SourceParseRunId(reader.GetGuid(0)),
            new FetchId(reader.GetGuid(1)),
            new PayloadId(reader.GetGuid(2)),
            new SourceId(reader.GetGuid(3)),
            new ShardId(reader.GetGuid(4)),
            new EndpointId(reader.GetGuid(5)),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.GetFieldValue<DateTimeOffset>(15),
            reader.GetFieldValue<byte[]>(16));
    }
}
