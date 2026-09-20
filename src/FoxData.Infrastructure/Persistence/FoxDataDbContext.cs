using Microsoft.EntityFrameworkCore;

namespace FoxData.Infrastructure.Persistence;

public sealed class FoxDataDbContext(DbContextOptions<FoxDataDbContext> options) : DbContext(options);
