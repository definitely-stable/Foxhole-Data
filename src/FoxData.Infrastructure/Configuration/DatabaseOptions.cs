namespace FoxData.Infrastructure.Configuration;

public sealed class DatabaseOptions
{
    public const string ConnectionStringName = "FoxData";

    public string ConnectionString { get; set; } = string.Empty;
}
