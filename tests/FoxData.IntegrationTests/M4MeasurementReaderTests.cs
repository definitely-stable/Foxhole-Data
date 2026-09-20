using System.Security.Cryptography;
using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
using FoxData.Application.Sources;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;
using FoxData.Infrastructure.Evidence;
using FoxData.Infrastructure.Ingestion;
using FoxData.Infrastructure.Persistence;
using FoxData.Infrastructure.Sources;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class M4MeasurementReaderTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ReaderStreamsRequestedSourceAndWindowFromDurableEvidence()
    {
        await MigrateAsync();
        await ResetAsync();

        await using var dataSource =
            NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry =
            new SourceRegistry(new PostgresSourceRegistryStore(dataSource));
        var ingestion =
            new IngestionKernel(new PostgresIngestionKernelStore(dataSource));
        var evidence =
            new EvidenceKernel(new PostgresEvidenceKernelStore(dataSource));
        var measurement =
            new PostgresSourceMeasurementReader(dataSource);

        var officialEndpoint = await RegisterEndpointAsync(
            registry,
            "official-war-api",
            "Official War API");
        var otherEndpoint = await RegisterEndpointAsync(
            registry,
            "other-source",
            "Other Source");

        var observedAt = DateTimeOffset.UtcNow;
        var officialBody = "official"u8.ToArray();
        var otherBody = "other"u8.ToArray();

        var officialCapture = await CaptureAsync(
            ingestion,
            evidence,
            officialEndpoint,
            "m4:official",
            observedAt,
            officialBody);
        _ = await CaptureAsync(
            ingestion,
            evidence,
            otherEndpoint,
            "m4:other",
            observedAt,
            otherBody);

        var fetches = new List<SourceMeasurementFetch>();
        await foreach (var fetch in measurement.ReadFetchesAsync(
            "official-war-api",
            observedAt.AddSeconds(-1),
            observedAt.AddSeconds(1),
            TestContext.Current.CancellationToken))
        {
            fetches.Add(fetch);
        }

        var measuredFetch = Assert.Single(fetches);
        Assert.Equal(officialCapture.Fetch!.Id, measuredFetch.FetchId);
        Assert.Equal("official-war-api", measuredFetch.SourceKey);
        Assert.Equal("live-1", measuredFetch.ShardKey);
        Assert.Equal("live", measuredFetch.Environment);
        Assert.Equal("runtime-war-state", measuredFetch.CapabilityKey);
        Assert.Equal("war", measuredFetch.SemanticKey);
        Assert.Equal(200, measuredFetch.StatusCode);
        Assert.Equal(12, measuredFetch.DurationMs);
        Assert.Equal(officialBody.LongLength, measuredFetch.PayloadBytes);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(officialBody)).ToLowerInvariant(),
            measuredFetch.PayloadSha256Hex);
        Assert.Equal("etag-official", measuredFetch.SourceEtag);
        Assert.Equal("max-age=60", measuredFetch.CacheControl);

        var attempts = new List<SourceMeasurementAttempt>();
        await foreach (var attempt in measurement.ReadAttemptsAsync(
            "official-war-api",
            observedAt.AddMinutes(-1),
            observedAt.AddMinutes(1),
            TestContext.Current.CancellationToken))
        {
            attempts.Add(attempt);
        }

        var measuredAttempt = Assert.Single(attempts);
        Assert.Equal(officialCapture.Fetch.AttemptId, measuredAttempt.AttemptId);
        Assert.Equal(officialEndpoint, measuredAttempt.EndpointId);
        Assert.Equal("official-war-api", measuredAttempt.SourceKey);
        Assert.Equal("runtime-war-state", measuredAttempt.CapabilityKey);
        Assert.NotNull(measuredAttempt.ExchangeAuthorizedAt);
        Assert.NotNull(measuredAttempt.RawDurableAt);
    }

    [Fact]
    public async Task ReaderRejectsInvalidMeasurementWindow()
    {
        await using var dataSource =
            NpgsqlDataSource.Create(postgres.ConnectionString);
        var measurement =
            new PostgresSourceMeasurementReader(dataSource);
        var now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<ArgumentException>(
            async () =>
            {
                await foreach (var _ in measurement.ReadFetchesAsync(
                    "official-war-api",
                    now,
                    now,
                    TestContext.Current.CancellationToken))
                {
                }
            });
    }

    private static async Task<EndpointId> RegisterEndpointAsync(
        SourceRegistry registry,
        string sourceKey,
        string displayName)
    {
        var source = await registry.RegisterSourceAsync(
            sourceKey,
            displayName,
            TestContext.Current.CancellationToken);
        var shard = await registry.RegisterShardAsync(
            source.Resource.Id,
            "live-1",
            "Live 1",
            "live",
            TestContext.Current.CancellationToken);
        var endpoint = await registry.RegisterEndpointAsync(
            shard.Resource.Id,
            "runtime-war-state",
            "war",
            TestContext.Current.CancellationToken);

        return endpoint.Resource.Id;
    }

    private static async Task<CaptureResult> CaptureAsync(
        IngestionKernel ingestion,
        EvidenceKernel evidence,
        EndpointId endpointId,
        string idempotencyKey,
        DateTimeOffset observedAt,
        byte[] body)
    {
        var workerId = WorkerInstanceId.New();
        var scheduledAt = DateTimeOffset.UtcNow.AddSeconds(-1);

        var queued = await ingestion.EnqueueAsync(
            endpointId,
            idempotencyKey,
            scheduledAt,
            scheduledAt,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(JobEnqueueStatus.Created, queued.Status);

        var claim = await ingestion.ClaimNextAsync(
            workerId,
            TimeSpan.FromMinutes(2),
            TestContext.Current.CancellationToken);
        Assert.True(claim.Claimed);

        var attemptId = IngestionAttemptId.New();
        var begun = await ingestion.BeginAttemptAsync(
            attemptId,
            claim.Job!.Id,
            workerId,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);
        Assert.Equal(BeginAttemptStatus.Started, begun.Status);

        var fenced = await ingestion.AcquireEndpointFenceAsync(
            attemptId,
            workerId,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);
        Assert.Equal(FenceAcquireStatus.AcquiredNow, fenced.Status);

        var authorized = await ingestion.AuthorizeExchangeAsync(
            attemptId,
            workerId,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            ExchangeAuthorizationStatus.AuthorizedNow,
            authorized.Status);

        var observation = new SourceResponseObservation(
            observedAt.AddMilliseconds(-12),
            observedAt.AddMilliseconds(-8),
            observedAt,
            "war-api",
            200,
            "application/json",
            null,
            body.LongLength,
            "etag-official",
            "max-age=60",
            observedAt.AddMinutes(1),
            12,
            observedAt,
            0);

        var capture = await evidence.CaptureSourceResponseAsync(
            attemptId,
            endpointId,
            claim.Job.LeaseGeneration,
            fenced.Attempt!.FenceToken!.Value,
            observation,
            body,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStatus.CapturedCurrent, capture.Status);
        return capture;
    }

    private async Task MigrateAsync()
    {
        var options = new DbContextOptionsBuilder<FoxDataDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var context = new FoxDataDbContext(options);
        await context.Database.MigrateAsync(
            TestContext.Current.CancellationToken);
    }

    private async Task ResetAsync()
    {
        await using var dataSource =
            NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var command = dataSource.CreateCommand(
            """
            TRUNCATE TABLE
                evidence.source_parse_runs,
                ingest.endpoint_poll_state,
                evidence.fetches,
                evidence.payloads,
                ingest.endpoint_state,
                ingest.attempts,
                ingest.collection_jobs,
                sources.endpoints,
                sources.shards,
                sources.sources
            RESTART IDENTITY CASCADE;
            """);

        await command.ExecuteNonQueryAsync(
            TestContext.Current.CancellationToken);
    }
}
