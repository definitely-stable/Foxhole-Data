using System.Globalization;
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

        services.AddSingleton(
            new EvidenceKernelLimits(
                ReadInt64(
                    configuration,
                    "EvidenceKernel:MaxPayloadBytes",
                    EvidenceKernelLimits.DefaultMaxPayloadBytes)));

        services.AddSingleton(
            new IngestionRecoveryLimits(
                ReadInt32(
                    configuration,
                    "IngestionKernel:RecoveryBatchSize",
                    IngestionRecoveryLimits.DefaultBatchSize)));

        services.AddDbContext<FoxDataDbContext>((serviceProvider, options) =>
            options.UseNpgsql(serviceProvider.GetRequiredService<NpgsqlDataSource>()));

        services.AddScoped<ISourceRegistryStore, PostgresSourceRegistryStore>();
        services.AddScoped<SourceRegistry>();

        services.AddScoped<IIngestionKernelStore, PostgresIngestionKernelStore>();
        services.AddScoped<IngestionKernel>();

        services.AddScoped<IIngestionRecoveryStore, PostgresIngestionRecoveryStore>();
        services.AddScoped<IngestionRecovery>();

        services.AddScoped<IEvidenceKernelStore, PostgresEvidenceKernelStore>();
        services.AddScoped<EvidenceKernel>();
        services.AddScoped<IEndpointEvidenceReader, PostgresEndpointEvidenceReader>();

        services.AddScoped<IEndpointPollStateStore, PostgresEndpointPollStateStore>();
        services.AddScoped<ISourceParseRunStore, PostgresSourceParseRunStore>();
        services.AddScoped<ISourcePlanningReader, PostgresSourcePlanningReader>();
        services.AddScoped<ISourceMeasurementReader, PostgresSourceMeasurementReader>();

        services.AddSingleton<PostgresHealthCheck>();

        return services;
    }

    private static int ReadInt32(
        IConfiguration configuration,
        string key,
        int defaultValue)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{key}' must be a valid integer.");
        }

        return parsed;
    }

    private static long ReadInt64(
        IConfiguration configuration,
        string key,
        long defaultValue)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{key}' must be a valid 64-bit integer.");
        }

        return parsed;
    }
}
