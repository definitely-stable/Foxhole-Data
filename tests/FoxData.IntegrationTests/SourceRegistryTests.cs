using FoxData.Application.Sources;
using FoxData.Infrastructure.Persistence;
using FoxData.Infrastructure.Sources;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class SourceRegistryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ConcurrentSourceRegistrationConvergesOnOneIdentity()
    {
        await MigrateAsync();

        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry = new SourceRegistry(new PostgresSourceRegistryStore(dataSource));

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => registry.RegisterSourceAsync(
                "fixture",
                "Fixture Source",
                TestContext.Current.CancellationToken))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Single(results, result => result.Status is RegistryRegistrationStatus.Created);
        Assert.Equal(
            7,
            results.Count(result => result.Status is RegistryRegistrationStatus.Existing));
        Assert.Single(results.Select(result => result.Resource.Id).Distinct());
    }

    [Fact]
    public async Task SameSourceKeyWithDifferentMetadataIsConflict()
    {
        await MigrateAsync();

        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry = new SourceRegistry(new PostgresSourceRegistryStore(dataSource));

        var created = await registry.RegisterSourceAsync(
            "fixture",
            "Fixture Source",
            TestContext.Current.CancellationToken);

        var conflict = await registry.RegisterSourceAsync(
            "fixture",
            "Different Source",
            TestContext.Current.CancellationToken);

        Assert.Equal(RegistryRegistrationStatus.Created, created.Status);
        Assert.Equal(RegistryRegistrationStatus.Conflict, conflict.Status);
        Assert.Equal(created.Resource.Id, conflict.Resource.Id);
        Assert.Equal("Fixture Source", conflict.Resource.DisplayName);
    }

    [Fact]
    public async Task ShardAndEndpointRegistrationAreIdempotentAndEndpointStateIsCreated()
    {
        await MigrateAsync();

        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry = new SourceRegistry(new PostgresSourceRegistryStore(dataSource));

        var source = await registry.RegisterSourceAsync(
            "fixture",
            "Fixture Source",
            TestContext.Current.CancellationToken);

        var shard = await registry.RegisterShardAsync(
            source.Resource.Id,
            "live-1",
            "Live 1",
            "live",
            TestContext.Current.CancellationToken);

        var shardAgain = await registry.RegisterShardAsync(
            source.Resource.Id,
            "live-1",
            "Live 1",
            "live",
            TestContext.Current.CancellationToken);

        var endpoint = await registry.RegisterEndpointAsync(
            shard.Resource.Id,
            "runtime-war-state",
            "war/current",
            TestContext.Current.CancellationToken);

        var endpointAgain = await registry.RegisterEndpointAsync(
            shard.Resource.Id,
            "runtime-war-state",
            "war/current",
            TestContext.Current.CancellationToken);

        var endpointConflict = await registry.RegisterEndpointAsync(
            shard.Resource.Id,
            "different-capability",
            "war/current",
            TestContext.Current.CancellationToken);

        Assert.Equal(RegistryRegistrationStatus.Created, shard.Status);
        Assert.Equal(RegistryRegistrationStatus.Existing, shardAgain.Status);
        Assert.Equal(shard.Resource.Id, shardAgain.Resource.Id);

        Assert.Equal(RegistryRegistrationStatus.Created, endpoint.Status);
        Assert.Equal(RegistryRegistrationStatus.Existing, endpointAgain.Status);
        Assert.Equal(RegistryRegistrationStatus.Conflict, endpointConflict.Status);
        Assert.Equal(endpoint.Resource.Id, endpointAgain.Resource.Id);
        Assert.Equal(endpoint.Resource.Id, endpointConflict.Resource.Id);

        await using var stateCommand = dataSource.CreateCommand(
            "SELECT fence_token FROM ingest.endpoint_state WHERE endpoint_id = @endpoint_id;");
        stateCommand.Parameters.AddWithValue("endpoint_id", endpoint.Resource.Id.Value);

        var fenceToken = await stateCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0L, Assert.IsType<long>(fenceToken));
    }

    [Fact]
    public async Task SameShardKeyWithDifferentEnvironmentIsConflict()
    {
        await MigrateAsync();

        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry = new SourceRegistry(new PostgresSourceRegistryStore(dataSource));

        var source = await registry.RegisterSourceAsync(
            "fixture",
            "Fixture Source",
            TestContext.Current.CancellationToken);

        var created = await registry.RegisterShardAsync(
            source.Resource.Id,
            "live-1",
            "Live 1",
            "live",
            TestContext.Current.CancellationToken);

        var conflict = await registry.RegisterShardAsync(
            source.Resource.Id,
            "live-1",
            "Live 1",
            "dev",
            TestContext.Current.CancellationToken);

        Assert.Equal(RegistryRegistrationStatus.Created, created.Status);
        Assert.Equal(RegistryRegistrationStatus.Conflict, conflict.Status);
        Assert.Equal(created.Resource.Id, conflict.Resource.Id);
        Assert.Equal("live", conflict.Resource.Environment);
    }

    private async Task MigrateAsync()
    {
        var options = new DbContextOptionsBuilder<FoxDataDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var context = new FoxDataDbContext(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }
}
