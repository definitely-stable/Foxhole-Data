using Microsoft.Extensions.Options;
using Npgsql;

namespace FoxData.Infrastructure.Configuration;

public sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return ValidateOptionsResult.Fail(
                $"ConnectionStrings:{DatabaseOptions.ConnectionStringName} is required.");
        }

        try
        {
            _ = new NpgsqlConnectionStringBuilder(options.ConnectionString);
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException exception)
        {
            return ValidateOptionsResult.Fail($"Invalid PostgreSQL connection string: {exception.Message}");
        }
    }
}
