using Microsoft.EntityFrameworkCore;

namespace FoxData.Infrastructure.Persistence;

public sealed class FoxDataDbContext(DbContextOptions<FoxDataDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        M2ModelConfiguration.Configure(modelBuilder);
        M3ModelConfiguration.Configure(modelBuilder);
        M4ModelConfiguration.Configure(modelBuilder);
        M5ModelConfiguration.Configure(modelBuilder);

        base.OnModelCreating(modelBuilder);
    }
}
