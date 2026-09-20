using System.Data;
using Npgsql;

namespace FoxData.Infrastructure.Sources;

public sealed record SourceMeasurementRelationSize(
    string SchemaName,
    string RelationName,
    long TotalBytes,
    long TableBytesIncludingToast,
    long IndexBytes,
    long ToastBytes);

public sealed record SourceMeasurementStorageSnapshot(
    DateTimeOffset CapturedAt,
    long DatabaseBytes,
    IReadOnlyList<SourceMeasurementRelationSize> Relations);

public sealed class PostgresSourceMeasurementStorageReader(
    NpgsqlDataSource dataSource)
{
    public async Task<SourceMeasurementStorageSnapshot> ReadAsync(
        CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);

        DateTimeOffset capturedAt;
        long databaseBytes;

        await using (var database = connection.CreateCommand())
        {
            database.CommandText =
                """
                SELECT clock_timestamp(), pg_database_size(current_database());
                """;

            await using var reader = await database.ExecuteReaderAsync(
                CommandBehavior.SingleRow,
                cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "PostgreSQL returned no database-size measurement.");
            }

            capturedAt = reader.GetFieldValue<DateTimeOffset>(0);
            databaseBytes = reader.GetInt64(1);
        }

        var relations = new List<SourceMeasurementRelationSize>(6);

        await using (var relation = connection.CreateCommand())
        {
            relation.CommandText =
                """
                WITH requested(schema_name, relation_name) AS (
                    VALUES
                        ('evidence', 'fetches'),
                        ('evidence', 'payloads'),
                        ('evidence', 'source_parse_runs'),
                        ('evidence', 'source_schedule_decisions'),
                        ('ingest', 'collection_jobs'),
                        ('ingest', 'attempts')
                )
                SELECT
                    requested.schema_name,
                    requested.relation_name,
                    pg_total_relation_size(class.oid),
                    pg_table_size(class.oid),
                    pg_indexes_size(class.oid),
                    CASE
                        WHEN class.reltoastrelid = 0 THEN 0
                        ELSE pg_total_relation_size(class.reltoastrelid)
                    END
                FROM requested
                INNER JOIN pg_namespace AS namespace
                    ON namespace.nspname = requested.schema_name
                INNER JOIN pg_class AS class
                    ON class.relnamespace = namespace.oid
                   AND class.relname = requested.relation_name
                ORDER BY
                    requested.schema_name,
                    requested.relation_name;
                """;

            await using var reader = await relation.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess,
                cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                relations.Add(
                    new SourceMeasurementRelationSize(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4),
                        reader.GetInt64(5)));
            }
        }

        if (relations.Count != 6)
        {
            throw new InvalidOperationException(
                $"Expected six M4 storage relations but measured {relations.Count}.");
        }

        return new SourceMeasurementStorageSnapshot(
            capturedAt,
            databaseBytes,
            relations);
    }
}
