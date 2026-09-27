using System.Data;
using FoxData.Application.Canonical;
using FoxData.Core.Runtime;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Canonical;

public sealed class PostgresWarRegionReader(NpgsqlDataSource dataSource)
    : IWarRegionReader
{
    public async Task<WarRegionDescriptor?> GetAsync(
        WarId warId,
        string sourceMapName,
        CancellationToken cancellationToken)
    {
        if (warId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "War identifier must not be empty.",
                nameof(warId));
        }

        if (string.IsNullOrWhiteSpace(sourceMapName) ||
            sourceMapName.Length > 256 ||
            !string.Equals(
                sourceMapName,
                sourceMapName.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Source map name must be non-empty, already trimmed, and at most 256 characters.",
                nameof(sourceMapName));
        }

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, war_id, region_id, source_map_name, source_region_id,
                first_seen_at, last_seen_at, created_at
            FROM runtime.war_regions
            WHERE war_id = @war_id
              AND source_map_name = @source_map_name;
            """;

        command.Parameters.Add("war_id", NpgsqlDbType.Uuid).Value =
            warId.Value;
        command.Parameters.Add("source_map_name", NpgsqlDbType.Text).Value =
            sourceMapName;

        await using var result = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await result.ReadAsync(cancellationToken))
        {
            return null;
        }

        return Read(result);
    }

    internal static WarRegionDescriptor Read(NpgsqlDataReader reader) =>
        new(
            new WarRegionId(reader.GetGuid(0)),
            new WarId(reader.GetGuid(1)),
            new RegionId(reader.GetGuid(2)),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));
}
