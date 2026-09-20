using System.Net;
using FoxData.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class ApiHealthTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task LivenessDoesNotDependOnDatabaseConnectivity()
    {
        await using var factory = CreateFactory(
            "Host=127.0.0.1;Port=1;Database=foxdata;Username=foxdata;Password=dev;Timeout=1;Command Timeout=1");

        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReadinessIsHealthyWithCurrentSchema()
    {
        await MigrateAsync(postgres.ConnectionString);

        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReadinessIsUnhealthyWhenPostgresIsReachableButSchemaIsPending()
    {
        var emptyDatabaseConnection = await CreateEmptyDatabaseAsync();

        await using var factory = CreateFactory(emptyDatabaseConnection);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ReadinessIsUnhealthyWhenPostgresIsUnavailable()
    {
        await using var factory = CreateFactory(
            "Host=127.0.0.1;Port=1;Database=foxdata;Username=foxdata;Password=dev;Timeout=1;Command Timeout=1");

        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private async Task<string> CreateEmptyDatabaseAsync()
    {
        var adminBuilder = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = "postgres",
        };

        var databaseName = $"foxdata_pending_{Guid.NewGuid():N}";

        await using var adminDataSource = NpgsqlDataSource.Create(adminBuilder.ConnectionString);
        await using var command = adminDataSource.CreateCommand($"CREATE DATABASE \"{databaseName}\";");

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        var targetBuilder = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = databaseName,
        };

        return targetBuilder.ConnectionString;
    }

    private static async Task MigrateAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<FoxDataDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var context = new FoxDataDbContext(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    private static WebApplicationFactory<Program> CreateFactory(string connectionString)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:FoxData"] = connectionString,
                        });
                });
            });
    }
}
