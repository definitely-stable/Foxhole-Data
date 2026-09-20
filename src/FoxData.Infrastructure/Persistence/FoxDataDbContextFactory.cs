using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FoxData.Infrastructure.Persistence;

public sealed class FoxDataDbContextFactory : IDesignTimeDbContextFactory<FoxDataDbContext>
{
    private const string EnvironmentVariable = "FOXDATA_DESIGN_CONNECTION";

    public FoxDataDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} must be set for design-time EF Core operations.");
        }

        var options = new DbContextOptionsBuilder<FoxDataDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new FoxDataDbContext(options);
    }
}
