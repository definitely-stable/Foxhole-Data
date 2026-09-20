using Testcontainers.PostgreSql;

namespace FoxData.RecoveryTests;

public sealed class RecoveryPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.6-bookworm")
        .WithDatabase("foxdata_recovery")
        .WithUsername("foxdata")
        .WithPassword("foxdata_recovery")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
