using FoxData.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class PostgresConnectivityTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ServerMajorVersionIs18()
    {
        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var command = dataSource.CreateCommand("SHOW server_version_num");

        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        var serverVersionNumber = Assert.IsType<string>(value);

        Assert.StartsWith("18", serverVersionNumber);
    }

    [Fact]
    public async Task DbContextCanConnectAsynchronously()
    {
        var options = new DbContextOptionsBuilder<FoxDataDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var dbContext = new FoxDataDbContext(options);

        Assert.True(await dbContext.Database.CanConnectAsync(TestContext.Current.CancellationToken));
    }
}
