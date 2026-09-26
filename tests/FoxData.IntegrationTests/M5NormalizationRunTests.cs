using FoxData.Application.Canonical;
using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
using FoxData.Application.Sources;
using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Infrastructure.Canonical;
using FoxData.Infrastructure.Evidence;
using FoxData.Infrastructure.Ingestion;
using FoxData.Infrastructure.Persistence;
using FoxData.Infrastructure.Sources;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class M5NormalizationRunTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task NormalizationRunIsIdempotentForEquivalentReplay()
    {
        await using var fixture = await CreateFixtureAsync("idempotent");
        var parseRun = await fixture.CreateParsedRunAsync("body");

        var startedAt = DateTimeOffset.UtcNow;
        var write = new NormalizationRunWrite(
            parseRun.Id,
            "war-normalizer@1",
            NormalizationRunOutcome.Normalized,
            null,
            startedAt,
            startedAt.AddMilliseconds(2));

        var first = await fixture.Normalization.RecordAsync(
            write,
            TestContext.Current.CancellationToken);

        var replay = write with
        {
            StartedAt = startedAt.AddMinutes(1),
            CompletedAt = startedAt.AddMinutes(1).AddMilliseconds(2),
        };

        var repeated = await fixture.Normalization.RecordAsync(
            replay,
            TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, repeated.Id);

        var loaded = await fixture.Normalization.GetAsync(
            parseRun.Id,
            "war-normalizer@1",
            TestContext.Current.CancellationToken);

        Assert.Equal(first, loaded);
    }

    [Fact]
    public async Task NormalizationRunRejectsConflictingReplay()
    {
        await using var fixture = await CreateFixtureAsync("conflict");
        var parseRun = await fixture.CreateParsedRunAsync("body");

        var startedAt = DateTimeOffset.UtcNow;
        var write = new NormalizationRunWrite(
            parseRun.Id,
            "war-normalizer@1",
            NormalizationRunOutcome.Normalized,
            null,
            startedAt,
            startedAt.AddMilliseconds(2));

        _ = await fixture.Normalization.RecordAsync(
            write,
            TestContext.Current.CancellationToken);

        var conflicting = write with
        {
            Outcome = NormalizationRunOutcome.Rejected,
            ErrorCode = "missing_identity",
        };

        await Assert.ThrowsAsync<CanonicalStateIntegrityException>(
            () => fixture.Normalization.RecordAsync(
                conflicting,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NormalizationRunRequiresExistingSourceParseRun()
    {
        await MigrateAsync();

        await using var dataSource =
            NpgsqlDataSource.Create(postgres.ConnectionString);
        var kernel = new NormalizationKernel(
            new PostgresNormalizationRunStore(dataSource));

        var now = DateTimeOffset.UtcNow;
        var write = new NormalizationRunWrite(
            SourceParseRunId.New(),
            "war-normalizer@1",
            NormalizationRunOutcome.Failed,
            "missing_parse",
            now,
            now);

        await Assert.ThrowsAsync<CanonicalStateIntegrityException>(
            () => kernel.RecordAsync(
                write,
                TestContext.Current.CancellationToken));
    }

    private async Task<M5Fixture> CreateFixtureAsync(string scenario)
    {
        await MigrateAsync();
        await ResetAsync();

        var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry = new SourceRegistry(
            new PostgresSourceRegistryStore(dataSource));

        var source = await registry.RegisterSourceAsync(
            $"m5-{scenario}",
            $"M5 {scenario}",
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

        return new M5Fixture(
            dataSource,
            new IngestionKernel(new PostgresIngestionKernelStore(dataSource)),
            new EvidenceKernel(new PostgresEvidenceKernelStore(dataSource)),
            new PostgresSourceParseRunStore(dataSource),
            new NormalizationKernel(
                new PostgresNormalizationRunStore(dataSource)),
            endpoint.Resource.Id);
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
                evidence.normalization_runs,
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

    private sealed class M5Fixture(
        NpgsqlDataSource dataSource,
        IngestionKernel ingestion,
        EvidenceKernel evidence,
        ISourceParseRunStore parseRuns,
        NormalizationKernel normalization,
        FoxData.Core.Sources.EndpointId endpointId) : IAsyncDisposable
    {
        public IngestionKernel Ingestion { get; } = ingestion;
        public EvidenceKernel Evidence { get; } = evidence;
        public ISourceParseRunStore ParseRuns { get; } = parseRuns;
        public NormalizationKernel Normalization { get; } = normalization;
        public FoxData.Core.Sources.EndpointId EndpointId { get; } = endpointId;

        public async Task<SourceParseRunDescriptor> CreateParsedRunAsync(
            string key)
        {
            var scheduledAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            var queued = await Ingestion.EnqueueAsync(
                EndpointId,
                key,
                scheduledAt,
                scheduledAt,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(JobEnqueueStatus.Created, queued.Status);

            var workerId = WorkerInstanceId.New();
            var claim = await Ingestion.ClaimNextAsync(
                workerId,
                TimeSpan.FromMinutes(5),
                TestContext.Current.CancellationToken);
            Assert.True(claim.Claimed);

            var attemptId = IngestionAttemptId.New();
            var begun = await Ingestion.BeginAttemptAsync(
                attemptId,
                claim.Job!.Id,
                workerId,
                claim.Job.LeaseGeneration,
                TestContext.Current.CancellationToken);
            Assert.Equal(BeginAttemptStatus.Started, begun.Status);

            var fenced = await Ingestion.AcquireEndpointFenceAsync(
                attemptId,
                workerId,
                claim.Job.LeaseGeneration,
                TestContext.Current.CancellationToken);
            Assert.Equal(FenceAcquireStatus.AcquiredNow, fenced.Status);

            var authorized = await Ingestion.AuthorizeExchangeAsync(
                attemptId,
                workerId,
                claim.Job.LeaseGeneration,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                ExchangeAuthorizationStatus.AuthorizedNow,
                authorized.Status);

            var now = DateTimeOffset.UtcNow;
            var body = "{}"u8.ToArray();
            var capture = await Evidence.CaptureSourceResponseAsync(
                attemptId,
                EndpointId,
                claim.Job.LeaseGeneration,
                fenced.Attempt!.FenceToken!.Value,
                new SourceResponseObservation(
                    RequestStartedAt: now.AddMilliseconds(-2),
                    ResponseStartedAt: now.AddMilliseconds(-1),
                    RetrievedAt: now,
                    TransportKind: "war-api",
                    StatusCode: 200,
                    MediaType: "application/json",
                    ContentEncoding: null,
                    DeclaredLength: body.LongLength,
                    SourceEtag: "\"m5\"",
                    CacheControl: "max-age=60",
                    ExpiresAt: now.AddMinutes(1),
                    DurationMs: 2),
                body,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(CaptureStatus.CapturedCurrent, capture.Status);

            var parseStartedAt = now.AddMilliseconds(1);
            return await ParseRuns.RecordAsync(
                new SourceParseRunWrite(
                    capture.Fetch!.Id,
                    "runtime-war-state",
                    "warapi-adapter@1",
                    "warapi-parser@1",
                    "json-shape@1",
                    "shape-m5",
                    "parsed",
                    0,
                    0,
                    null,
                    parseStartedAt,
                    parseStartedAt.AddMilliseconds(1)),
                TestContext.Current.CancellationToken);
        }

        public ValueTask DisposeAsync() => dataSource.DisposeAsync();
    }
}
