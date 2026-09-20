using FoxData.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FoxData.UnitTests;

public sealed class FoxDataDbContextFactoryTests
{
    [Fact]
    public void ExplicitConnectionArgumentOverridesEnvironmentFallback()
    {
        const string explicitConnection =
            "Host=explicit.example;Port=5432;Database=foxdata;Username=foxdata;Password=test";
        const string environmentConnection =
            "Host=environment.example;Port=5432;Database=foxdata;Username=foxdata;Password=test";

        var previous = Environment.GetEnvironmentVariable("FOXDATA_DESIGN_CONNECTION");

        try
        {
            Environment.SetEnvironmentVariable(
                "FOXDATA_DESIGN_CONNECTION",
                environmentConnection);

            using var context = new FoxDataDbContextFactory().CreateDbContext(
                ["--connection", explicitConnection]);

            Assert.Equal(
                explicitConnection,
                context.Database.GetDbConnection().ConnectionString);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "FOXDATA_DESIGN_CONNECTION",
                previous);
        }
    }

    [Fact]
    public void EqualsFormConnectionArgumentIsSupported()
    {
        const string connection =
            "Host=equals.example;Port=5432;Database=foxdata;Username=foxdata;Password=test";

        using var context = new FoxDataDbContextFactory().CreateDbContext(
            [$"--connection={connection}"]);

        Assert.Equal(
            connection,
            context.Database.GetDbConnection().ConnectionString);
    }
}
