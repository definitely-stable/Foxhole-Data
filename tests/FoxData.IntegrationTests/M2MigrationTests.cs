using FoxData.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class M2MigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task MigrationCreatesOnlyM2SchemasAndTables()
    {
        await MigrateAsync();

        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var command = dataSource.CreateCommand(
            """
            SELECT table_schema, table_name
            FROM information_schema.tables
            WHERE table_schema IN ('sources', 'ingest', 'evidence', 'runtime', 'identity', 'quality', 'distribution')
              AND table_type = 'BASE TABLE'
            ORDER BY table_schema, table_name;
            """);

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        var tables = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
        }

        Assert.Equal(
            [
                "evidence.fetches",
                "evidence.payloads",
                "ingest.attempts",
                "ingest.collection_jobs",
                "ingest.endpoint_state",
                "sources.endpoints",
                "sources.shards",
                "sources.sources",
            ],
            tables);
    }

    private async Task MigrateAsync()
    {
        var options = new DbContextOptionsBuilder<FoxDataDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var context = new FoxDataDbContext(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }
}
