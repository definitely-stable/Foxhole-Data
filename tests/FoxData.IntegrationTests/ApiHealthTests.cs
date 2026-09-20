using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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
    public async Task ReadinessIsHealthyWithPostgres()
    {
        await using var factory = CreateFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
