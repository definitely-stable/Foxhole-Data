using Npgsql;
using Testcontainers.PostgreSql;

namespace FoxData.IntegrationTests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.6-bookworm")
        .WithDatabase("foxdata_test")
        .WithUsername("foxdata")
        .WithPassword("foxdata_test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task<string> CreateDatabaseConnectionStringAsync(
        CancellationToken cancellationToken = default)
    {
        var databaseName = $"foxdata_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = "postgres",
        };

        await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {databaseName};";
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;
    }

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
