using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
using FoxData.Application.Sources;
using FoxData.Core.Ingestion;
using FoxData.Infrastructure.Evidence;
using FoxData.Infrastructure.Ingestion;
using FoxData.Infrastructure.Persistence;
using FoxData.Infrastructure.Sources;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class M3SourceStateTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task EvidenceReaderResolves304ToBodyBearingRepresentation()
    {
        await using var fixture = await CreateFixtureAsync("reader-304");

        var first = await fixture.CreateAuthorizedAttemptAsync("body");
        var firstCapture = await fixture.Evidence.CaptureSourceResponseAsync(
            first.AttemptId,
            fixture.EndpointId,
            first.LeaseGeneration,
            first.FenceToken,
            CreateObservation(200, 3, "\"v1\""),
            new byte[] { 1, 2, 3 },
            cancellationToken: TestContext.Current.CancellationToken);

        var second = await fixture.CreateAuthorizedAttemptAsync("validation");
        var secondCapture = await fixture.Evidence.CaptureSourceResponseAsync(
            second.AttemptId,
            fixture.EndpointId,
            second.LeaseGeneration,
            second.FenceToken,
            CreateObservation(304, null, "\"v1\""),
            body: null,
            priorFetchId: firstCapture.Fetch!.Id,
            cancellationToken: TestContext.Current.CancellationToken);

        var snapshot = await fixture.Reader.GetCurrentAsync(
            fixture.EndpointId,
            TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot);
        Assert.Equal(secondCapture.Fetch!.Id, snapshot.CurrentFetch.Id);
        Assert.Null(snapshot.CurrentFetch.PayloadId);
        Assert.Equal(firstCapture.Fetch.Id, snapshot.RepresentationFetch!.Id);
        Assert.Equal(firstCapture.Payload!.Id, snapshot.RepresentationPayload!.Id);
        Assert.True(snapshot.RepresentationPayload.Body.Span.SequenceEqual(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task PollStateRequiresBodyBearingRepresentationFromSameEndpoint()
    {
        await using var fixture = await CreateFixtureAsync("poll-representation");

        var first = await fixture.CreateAuthorizedAttemptAsync("body");
        var firstCapture = await fixture.Evidence.CaptureSourceResponseAsync(
            first.AttemptId,
            fixture.EndpointId,
            first.LeaseGeneration,
            first.FenceToken,
            CreateObservation(200, 1, "\"v1\""),
            new byte[] { 7 },
            cancellationToken: TestContext.Current.CancellationToken);

        var second = await fixture.CreateAuthorizedAttemptAsync("validation");
        var secondCapture = await fixture.Evidence.CaptureSourceResponseAsync(
            second.AttemptId,
            fixture.EndpointId,
            second.LeaseGeneration,
            second.FenceToken,
            CreateObservation(304, null, "\"v1\""),
            body: null,
            priorFetchId: firstCapture.Fetch!.Id,
            cancellationToken: TestContext.Current.CancellationToken);

        var now = DateTimeOffset.UtcNow;
        var rejected = new EndpointPollStateWrite(
            fixture.EndpointId,
            secondCapture.Fetch!.Id,
            secondCapture.Fetch.Id,
            secondCapture.Fetch.Id,
            "\"v1\"",
            now,
            now.AddMinutes(1),
            null,
            now,
            now,
            0,
            "warapi-cache-policy@1");

        await Assert.ThrowsAsync<SourceStateIntegrityException>(
            () => fixture.PollState.PutAsync(
                rejected,
                TestContext.Current.CancellationToken));

        var accepted = rejected with
        {
            RepresentationFetchId = firstCapture.Fetch.Id,
        };

        var stored = await fixture.PollState.PutAsync(
            accepted,
            TestContext.Current.CancellationToken);

        Assert.Equal(firstCapture.Fetch.Id, stored.RepresentationFetchId);
        Assert.Equal(secondCapture.Fetch.Id, stored.LatestValidationFetchId);
        Assert.Equal("\"v1\"", stored.ValidatorEtag);
    }

    [Fact]
    public async Task ParseRunIsIdempotentForEquivalentReplay()
    {
        await using var fixture = await CreateFixtureAsync("parse-idempotent");

        var attempt = await fixture.CreateAuthorizedAttemptAsync("body");
        var capture = await fixture.Evidence.CaptureSourceResponseAsync(
            attempt.AttemptId,
            fixture.EndpointId,
            attempt.LeaseGeneration,
            attempt.FenceToken,
            CreateObservation(200, 2, "\"v1\""),
            new byte[] { (byte)'{', (byte)'}' },
            cancellationToken: TestContext.Current.CancellationToken);

        var started = DateTimeOffset.UtcNow;
        var write = new SourceParseRunWrite(
            capture.Fetch!.Id,
            "runtime-war-state",
            "warapi-adapter@1",
            "warapi-parser@1",
            "json-shape@1",
            new string('a', 64),
            "parsed",
            0,
            0,
            null,
            started,
            started.AddMilliseconds(1));

        var first = await fixture.ParseRuns.RecordAsync(
            write,
            TestContext.Current.CancellationToken);
        var replayedWrite = write with
        {
            StartedAt = started.AddMinutes(1),
            CompletedAt = started.AddMinutes(1).AddMilliseconds(1),
        };

        var repeated = await fixture.ParseRuns.RecordAsync(
            replayedWrite,
            TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, repeated.Id);

        var loaded = await fixture.ParseRuns.GetAsync(
            capture.Fetch.Id,
            "runtime-war-state",
            "warapi-parser@1",
            TestContext.Current.CancellationToken);

        Assert.Equal(first, loaded);
    }

    [Fact]
    public async Task ParseRunRejectsConflictingReplay()
    {
        await using var fixture = await CreateFixtureAsync("parse-conflict");

        var attempt = await fixture.CreateAuthorizedAttemptAsync("body");
        var capture = await fixture.Evidence.CaptureSourceResponseAsync(
            attempt.AttemptId,
            fixture.EndpointId,
            attempt.LeaseGeneration,
            attempt.FenceToken,
            CreateObservation(200, 1, "\"v1\""),
            new byte[] { 1 },
            cancellationToken: TestContext.Current.CancellationToken);

        var started = DateTimeOffset.UtcNow;
        var write = new SourceParseRunWrite(
            capture.Fetch!.Id,
            "runtime-war-state",
            "warapi-adapter@1",
            "warapi-parser@1",
            "json-shape@1",
            new string('b', 64),
            "parsed",
            0,
            0,
            null,
            started,
            started.AddMilliseconds(1));

        _ = await fixture.ParseRuns.RecordAsync(
            write,
            TestContext.Current.CancellationToken);

        var conflicting = write with
        {
            Outcome = "malformed_json",
            ErrorCode = "malformed_json",
        };

        await Assert.ThrowsAsync<SourceStateIntegrityException>(
            () => fixture.ParseRuns.RecordAsync(
                conflicting,
                TestContext.Current.CancellationToken));
    }

    private async Task<M3Fixture> CreateFixtureAsync(string scenario)
    {
        await MigrateAsync();
        await ResetAsync();

        var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry = new SourceRegistry(new PostgresSourceRegistryStore(dataSource));

        var source = await registry.RegisterSourceAsync(
            $"m3-{scenario}",
            $"M3 {scenario}",
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

        return new M3Fixture(
            dataSource,
            new IngestionKernel(new PostgresIngestionKernelStore(dataSource)),
            new EvidenceKernel(new PostgresEvidenceKernelStore(dataSource)),
            new PostgresEndpointEvidenceReader(dataSource),
            new PostgresEndpointPollStateStore(dataSource),
            new PostgresSourceParseRunStore(dataSource),
            endpoint.Resource.Id);
    }

    private async Task MigrateAsync()
    {
        var options = new DbContextOptionsBuilder<FoxDataDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var context = new FoxDataDbContext(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    private async Task ResetAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
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

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static SourceResponseObservation CreateObservation(
        int status,
        long? length,
        string etag)
    {
        var now = DateTimeOffset.UtcNow;

        return new SourceResponseObservation(
            now.AddMilliseconds(-2),
            now.AddMilliseconds(-1),
            now,
            "war-api",
            status,
            "application/json",
            null,
            length,
            etag,
            "max-age=60",
            now.AddSeconds(60),
            2);
    }

    private sealed class M3Fixture(
        NpgsqlDataSource dataSource,
        IngestionKernel ingestion,
        EvidenceKernel evidence,
        IEndpointEvidenceReader reader,
        IEndpointPollStateStore pollState,
        ISourceParseRunStore parseRuns,
        FoxData.Core.Sources.EndpointId endpointId) : IAsyncDisposable
    {
        public IngestionKernel Ingestion { get; } = ingestion;
        public EvidenceKernel Evidence { get; } = evidence;
        public IEndpointEvidenceReader Reader { get; } = reader;
        public IEndpointPollStateStore PollState { get; } = pollState;
        public ISourceParseRunStore ParseRuns { get; } = parseRuns;
        public FoxData.Core.Sources.EndpointId EndpointId { get; } = endpointId;

        public async Task<AuthorizedAttempt> CreateAuthorizedAttemptAsync(string key)
        {
            var scheduled = DateTimeOffset.UtcNow.AddSeconds(-1);
            var enqueue = await Ingestion.EnqueueAsync(
                EndpointId,
                key,
                scheduled,
                scheduled,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(JobEnqueueStatus.Created, enqueue.Status);

            var worker = WorkerInstanceId.New();
            var claim = await Ingestion.ClaimNextAsync(
                worker,
                TimeSpan.FromMinutes(5),
                TestContext.Current.CancellationToken);
            Assert.True(claim.Claimed);

            var attemptId = IngestionAttemptId.New();
            var begun = await Ingestion.BeginAttemptAsync(
                attemptId,
                claim.Job!.Id,
                worker,
                claim.Job.LeaseGeneration,
                TestContext.Current.CancellationToken);
            Assert.Equal(BeginAttemptStatus.Started, begun.Status);

            var fenced = await Ingestion.AcquireEndpointFenceAsync(
                attemptId,
                worker,
                claim.Job.LeaseGeneration,
                TestContext.Current.CancellationToken);
            Assert.Equal(FenceAcquireStatus.AcquiredNow, fenced.Status);

            var authorized = await Ingestion.AuthorizeExchangeAsync(
                attemptId,
                worker,
                claim.Job.LeaseGeneration,
                TestContext.Current.CancellationToken);
            Assert.Equal(ExchangeAuthorizationStatus.AuthorizedNow, authorized.Status);

            return new AuthorizedAttempt(
                attemptId,
                claim.Job.LeaseGeneration,
                fenced.Attempt!.FenceToken!.Value);
        }

        public ValueTask DisposeAsync() => dataSource.DisposeAsync();
    }

    private sealed record AuthorizedAttempt(
        IngestionAttemptId AttemptId,
        LeaseGeneration LeaseGeneration,
        FenceToken FenceToken);
}
