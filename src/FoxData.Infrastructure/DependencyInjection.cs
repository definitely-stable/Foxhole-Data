using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
using FoxData.Application.Sources;
using FoxData.Infrastructure.Configuration;
using FoxData.Infrastructure.Evidence;
using FoxData.Infrastructure.Health;
using FoxData.Infrastructure.Ingestion;
using FoxData.Infrastructure.Persistence;
using FoxData.Infrastructure.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FoxData.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFoxDataInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<DatabaseOptions>()
            .Configure(options =>
                options.ConnectionString =
                    configuration.GetConnectionString(DatabaseOptions.ConnectionStringName) ?? string.Empty)
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();

        services.AddSingleton(static serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            return new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
        });

        services.AddDbContext<FoxDataDbContext>((serviceProvider, options) =>
            options.UseNpgsql(serviceProvider.GetRequiredService<NpgsqlDataSource>()));

        services.AddScoped<ISourceRegistryStore, PostgresSourceRegistryStore>();
        services.AddScoped<SourceRegistry>();

        services.AddScoped<IIngestionKernelStore, PostgresIngestionKernelStore>();
        services.AddScoped<IngestionKernel>();

        services.AddScoped<IEvidenceKernelStore, PostgresEvidenceKernelStore>();
        services.AddScoped<EvidenceKernel>();

        services.AddSingleton<PostgresHealthCheck>();

        return services;
    }
}
