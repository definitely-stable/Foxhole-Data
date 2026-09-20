using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FoxData.Infrastructure.Persistence;

public sealed class FoxDataDbContextFactory : IDesignTimeDbContextFactory<FoxDataDbContext>
{
    private const string EnvironmentVariable = "FOXDATA_DESIGN_CONNECTION";
    private const string ConnectionArgument = "--connection";

    public FoxDataDbContext CreateDbContext(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var connectionString =
            TryGetConnectionArgument(args) ??
            Environment.GetEnvironmentVariable(EnvironmentVariable);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"A design-time connection must be supplied through {ConnectionArgument} or {EnvironmentVariable}.");
        }

        var options = new DbContextOptionsBuilder<FoxDataDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new FoxDataDbContext(options);
    }

    private static string? TryGetConnectionArgument(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            if (string.Equals(argument, ConnectionArgument, StringComparison.Ordinal))
            {
                if (index + 1 >= args.Length ||
                    string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    throw new ArgumentException(
                        $"{ConnectionArgument} requires a non-empty value.",
                        nameof(args));
                }

                return args[index + 1];
            }

            const string prefix = ConnectionArgument + "=";
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
            {
                var value = argument[prefix.Length..];

                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException(
                        $"{ConnectionArgument} requires a non-empty value.",
                        nameof(args));
                }

                return value;
            }
        }

        return null;
    }
}
