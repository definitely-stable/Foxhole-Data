using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
using FoxData.Application.Sources;
using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;
using FoxData.Infrastructure.Evidence;
using FoxData.Infrastructure.Ingestion;
using FoxData.Infrastructure.Persistence;
using FoxData.Infrastructure.Sources;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class EvidenceKernelTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task CurrentCapturePersistsEvidenceAndAdvancesAuthority()
    {
        await using var fixture = await CreateFixtureAsync("current");
        var authorized = await fixture.CreateAuthorizedAttemptAsync("job-current");

        var body = new byte[] { 1, 2, 3, 4 };
        var observation = CreateObservation(200, body.Length);

        var capture = await fixture.Evidence.CaptureSourceResponseAsync(
            authorized.AttemptId,
            fixture.EndpointId,
            authorized.LeaseGeneration,
            authorized.FenceToken,
            observation,
            body,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStatus.CapturedCurrent, capture.Status);
        Assert.True(capture.IsCurrentCapture);
        Assert.NotNull(capture.Fetch);
        Assert.NotNull(capture.Payload);
        Assert.Equal(PayloadHash.Compute(body), capture.Payload!.Hash);

        var storedFetch = await fixture.Evidence.GetAttemptEvidenceAsync(
            authorized.AttemptId,
            TestContext.Current.CancellationToken);
        var storedPayload = await fixture.Evidence.GetPayloadAsync(
            capture.Payload.Id,
            TestContext.Current.CancellationToken);
        var storedAttempt = await fixture.Ingestion.GetAttemptAsync(
            authorized.AttemptId,
            TestContext.Current.CancellationToken);
        var storedJob = await fixture.Ingestion.GetJobAsync(
            authorized.JobId,
            TestContext.Current.CancellationToken);

        Assert.Equal(capture.Fetch.Id, storedFetch!.Id);
        Assert.True(storedPayload!.Body.Span.SequenceEqual(body));
        Assert.Equal(IngestionAttemptState.Completed, storedAttempt!.State);
        Assert.Equal(CollectionJobState.Completed, storedJob!.State);
        Assert.Null(storedJob.LeaseOwnerId);
        Assert.Null(storedJob.LeaseExpiresAt);

        var authority = await fixture.ReadEndpointCaptureStateAsync();

        Assert.Null(authority.ActiveAttemptId);
        Assert.Equal(authorized.AttemptId, authority.LastCurrentCaptureAttemptId);
        Assert.Equal(authorized.FenceToken, authority.FenceToken);
    }

    [Fact]
    public async Task IdenticalBytesReusePayloadButKeepSeparateFetchObservations()
    {
        await using var fixture = await CreateFixtureAsync("dedupe");
        var body = new byte[] { 9, 8, 7, 6 };

        var first = await fixture.CreateAuthorizedAttemptAsync("job-first");
        var firstCapture = await fixture.Evidence.CaptureSourceResponseAsync(
            first.AttemptId,
            fixture.EndpointId,
            first.LeaseGeneration,
            first.FenceToken,
            CreateObservation(200, body.Length),
            body,
            cancellationToken: TestContext.Current.CancellationToken);

        var second = await fixture.CreateAuthorizedAttemptAsync("job-second");
        var secondCapture = await fixture.Evidence.CaptureSourceResponseAsync(
            second.AttemptId,
            fixture.EndpointId,
            second.LeaseGeneration,
            second.FenceToken,
            CreateObservation(200, body.Length),
            body,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStatus.CapturedCurrent, firstCapture.Status);
        Assert.Equal(CaptureStatus.CapturedCurrent, secondCapture.Status);
        Assert.False(firstCapture.PayloadDeduplicated);
        Assert.True(secondCapture.PayloadDeduplicated);
        Assert.Equal(firstCapture.Payload!.Id, secondCapture.Payload!.Id);
        Assert.NotEqual(firstCapture.Fetch!.Id, secondCapture.Fetch!.Id);

        var counts = await fixture.ReadEvidenceCountsAsync();

        Assert.Equal(1, counts.Payloads);
        Assert.Equal(2, counts.Fetches);
    }

    [Fact]
    public async Task NoBodyObservationCanReferencePriorRepresentation()
    {
        await using var fixture = await CreateFixtureAsync("no-body");

        var first = await fixture.CreateAuthorizedAttemptAsync("job-body");
        var firstCapture = await fixture.Evidence.CaptureSourceResponseAsync(
            first.AttemptId,
            fixture.EndpointId,
            first.LeaseGeneration,
            first.FenceToken,
            CreateObservation(200, 3),
            new byte[] { 1, 1, 1 },
            cancellationToken: TestContext.Current.CancellationToken);

        var second = await fixture.CreateAuthorizedAttemptAsync("job-validation");
        var secondCapture = await fixture.Evidence.CaptureSourceResponseAsync(
            second.AttemptId,
            fixture.EndpointId,
            second.LeaseGeneration,
            second.FenceToken,
            CreateObservation(304, null),
            body: null,
            priorFetchId: firstCapture.Fetch!.Id,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStatus.CapturedCurrent, secondCapture.Status);
        Assert.Null(secondCapture.Payload);
        Assert.Null(secondCapture.Fetch!.PayloadId);
        Assert.Equal(firstCapture.Fetch.Id, secondCapture.Fetch.PriorFetchId);

        var counts = await fixture.ReadEvidenceCountsAsync();

        Assert.Equal(1, counts.Payloads);
        Assert.Equal(2, counts.Fetches);
    }

    [Fact]
    public async Task RepeatedCaptureReconcilesByAttemptWithoutDuplicatingEvidence()
    {
        await using var fixture = await CreateFixtureAsync("reconcile");
        var authorized = await fixture.CreateAuthorizedAttemptAsync("job-reconcile");
        var body = new byte[] { 4, 4, 4 };

        var first = await fixture.Evidence.CaptureSourceResponseAsync(
            authorized.AttemptId,
            fixture.EndpointId,
            authorized.LeaseGeneration,
            authorized.FenceToken,
            CreateObservation(200, body.Length),
            body,
            cancellationToken: TestContext.Current.CancellationToken);

        var repeated = await fixture.Evidence.CaptureSourceResponseAsync(
            authorized.AttemptId,
            fixture.EndpointId,
            authorized.LeaseGeneration,
            authorized.FenceToken,
            CreateObservation(200, body.Length),
            body,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStatus.CapturedCurrent, first.Status);
        Assert.Equal(CaptureStatus.AlreadyCaptured, repeated.Status);
        Assert.Equal(first.Fetch!.Id, repeated.Fetch!.Id);
        Assert.Equal(first.Payload!.Id, repeated.Payload!.Id);

        var counts = await fixture.ReadEvidenceCountsAsync();

        Assert.Equal(1, counts.Payloads);
        Assert.Equal(1, counts.Fetches);
    }

    [Fact]
    public async Task OlderAuthorizedResponseIsCapturedLateAfterNewerFence()
    {
        await using var fixture = await CreateFixtureAsync("late");

        await fixture.EnqueueAsync("job-first");
        await fixture.EnqueueAsync("job-second");

        var first = await fixture.ClaimAndAuthorizeNextAsync();
        var second = await fixture.ClaimAndAuthorizeNextAsync();

        Assert.True(second.FenceToken.Value > first.FenceToken.Value);

        var late = await fixture.Evidence.CaptureSourceResponseAsync(
            first.AttemptId,
            fixture.EndpointId,
            first.LeaseGeneration,
            first.FenceToken,
            CreateObservation(200, 1),
            new byte[] { 1 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStatus.CapturedLate, late.Status);
        Assert.False(late.IsCurrentCapture);

        var beforeCurrent = await fixture.ReadEndpointCaptureStateAsync();

        Assert.Equal(second.AttemptId, beforeCurrent.ActiveAttemptId);
        Assert.Null(beforeCurrent.LastCurrentCaptureAttemptId);

        var current = await fixture.Evidence.CaptureSourceResponseAsync(
            second.AttemptId,
            fixture.EndpointId,
            second.LeaseGeneration,
            second.FenceToken,
            CreateObservation(200, 1),
            new byte[] { 2 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStatus.CapturedCurrent, current.Status);

        var firstAttempt = await fixture.Ingestion.GetAttemptAsync(
            first.AttemptId,
            TestContext.Current.CancellationToken);
        var authority = await fixture.ReadEndpointCaptureStateAsync();

        Assert.Equal(IngestionAttemptState.CapturedLate, firstAttempt!.State);
        Assert.Null(authority.ActiveAttemptId);
        Assert.Equal(second.AttemptId, authority.LastCurrentCaptureAttemptId);
        Assert.Equal(second.FenceToken, authority.FenceToken);
    }

    [Fact]
    public async Task PriorFetchFromAnotherEndpointIsRejected()
    {
        await using var firstFixture = await CreateFixtureAsync("prior-source");
        var first = await firstFixture.CreateAuthorizedAttemptAsync("job-source");
        var sourceCapture = await firstFixture.Evidence.CaptureSourceResponseAsync(
            first.AttemptId,
            firstFixture.EndpointId,
            first.LeaseGeneration,
            first.FenceToken,
            CreateObservation(200, 1),
            new byte[] { 7 },
            cancellationToken: TestContext.Current.CancellationToken);

        await using var secondFixture = await CreateAdditionalEndpointFixtureAsync(
            firstFixture,
            "prior-target");
        var second = await secondFixture.CreateAuthorizedAttemptAsync("job-target");

        var rejected = await secondFixture.Evidence.CaptureSourceResponseAsync(
            second.AttemptId,
            secondFixture.EndpointId,
            second.LeaseGeneration,
            second.FenceToken,
            CreateObservation(304, null),
            body: null,
            priorFetchId: sourceCapture.Fetch!.Id,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStatus.InvalidPriorFetch, rejected.Status);
        Assert.Null(rejected.Fetch);
    }

    private static SourceResponseObservation CreateObservation(int statusCode, long? declaredLength)
    {
        var now = DateTimeOffset.UtcNow;

        return new SourceResponseObservation(
            now.AddMilliseconds(-2),
            now.AddMilliseconds(-1),
            now,
            "fixture",
            statusCode,
            "application/json",
            null,
            declaredLength,
            "\"fixture-etag\"",
            "max-age=1",
            now.AddSeconds(1),
            2);
    }

    private async Task<EvidenceFixture> CreateFixtureAsync(string scenario)
    {
        await MigrateAsync();
        await ResetKernelDataAsync();

        var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry = new SourceRegistry(new PostgresSourceRegistryStore(dataSource));

        var source = await registry.RegisterSourceAsync(
            $"fixture-{scenario}",
            $"Fixture {scenario}",
            TestContext.Current.CancellationToken);
        var shard = await registry.RegisterShardAsync(
            source.Resource.Id,
            "test",
            "Test",
            "test",
            TestContext.Current.CancellationToken);
        var endpoint = await registry.RegisterEndpointAsync(
            shard.Resource.Id,
            "fixture",
            $"{scenario}/endpoint",
            TestContext.Current.CancellationToken);

        return new EvidenceFixture(
            dataSource,
            registry,
            new IngestionKernel(new PostgresIngestionKernelStore(dataSource)),
            new EvidenceKernel(new PostgresEvidenceKernelStore(dataSource)),
            source.Resource.Id,
            shard.Resource.Id,
            endpoint.Resource.Id);
    }

    private static async Task<EvidenceFixture> CreateAdditionalEndpointFixtureAsync(
        EvidenceFixture root,
        string scenario)
    {
        var endpoint = await root.Registry.RegisterEndpointAsync(
            root.ShardId,
            "fixture",
            $"{scenario}/endpoint",
            TestContext.Current.CancellationToken);

        return new EvidenceFixture(
            root.DataSource,
            root.Registry,
            root.Ingestion,
            root.Evidence,
            root.SourceId,
            root.ShardId,
            endpoint.Resource.Id,
            ownsDataSource: false);
    }

    private async Task MigrateAsync()
    {
        var options = new DbContextOptionsBuilder<FoxDataDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var context = new FoxDataDbContext(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    private async Task ResetKernelDataAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var command = dataSource.CreateCommand(
            """
            TRUNCATE TABLE
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

    private sealed class EvidenceFixture : IAsyncDisposable
    {
        private readonly bool _ownsDataSource;

        public EvidenceFixture(
            NpgsqlDataSource dataSource,
            SourceRegistry registry,
            IngestionKernel ingestion,
            EvidenceKernel evidence,
            SourceId sourceId,
            ShardId shardId,
            EndpointId endpointId,
            bool ownsDataSource = true)
        {
            DataSource = dataSource;
            Registry = registry;
            Ingestion = ingestion;
            Evidence = evidence;
            SourceId = sourceId;
            ShardId = shardId;
            EndpointId = endpointId;
            _ownsDataSource = ownsDataSource;
        }

        public NpgsqlDataSource DataSource { get; }

        public SourceRegistry Registry { get; }

        public IngestionKernel Ingestion { get; }

        public EvidenceKernel Evidence { get; }

        public SourceId SourceId { get; }

        public ShardId ShardId { get; }

        public EndpointId EndpointId { get; }

        public async Task EnqueueAsync(string idempotencyKey)
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);

            var result = await Ingestion.EnqueueAsync(
                EndpointId,
                idempotencyKey,
                now,
                now,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(JobEnqueueStatus.Created, result.Status);
        }

        public async Task<AuthorizedAttempt> CreateAuthorizedAttemptAsync(string idempotencyKey)
        {
            await EnqueueAsync(idempotencyKey);
            return await ClaimAndAuthorizeNextAsync();
        }

        public async Task<AuthorizedAttempt> ClaimAndAuthorizeNextAsync()
        {
            var worker = WorkerInstanceId.New();
            var claim = await Ingestion.ClaimNextAsync(
                worker,
                TimeSpan.FromMinutes(5),
                TestContext.Current.CancellationToken);

            Assert.True(claim.Claimed);

            var attemptId = IngestionAttemptId.New();
            var started = await Ingestion.BeginAttemptAsync(
                attemptId,
                claim.Job!.Id,
                worker,
                claim.Job.LeaseGeneration,
                TestContext.Current.CancellationToken);
            Assert.Equal(BeginAttemptStatus.Started, started.Status);

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
                claim.Job.Id,
                attemptId,
                claim.Job.LeaseGeneration,
                fenced.Attempt!.FenceToken!.Value);
        }

        public async Task<(long Payloads, long Fetches)> ReadEvidenceCountsAsync()
        {
            await using var command = DataSource.CreateCommand(
                """
                SELECT
                    (SELECT count(*) FROM evidence.payloads),
                    (SELECT count(*) FROM evidence.fetches);
                """);
            await using var reader = await command.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);

            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            return (reader.GetInt64(0), reader.GetInt64(1));
        }

        public async Task<EndpointCaptureState> ReadEndpointCaptureStateAsync()
        {
            await using var command = DataSource.CreateCommand(
                """
                SELECT fence_token, active_attempt_id, last_current_capture_attempt_id
                FROM ingest.endpoint_state
                WHERE endpoint_id = @endpoint_id;
                """);
            command.Parameters.AddWithValue("endpoint_id", EndpointId.Value);

            await using var reader = await command.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));

            return new EndpointCaptureState(
                new FenceToken(reader.GetInt64(0)),
                reader.IsDBNull(1) ? null : new IngestionAttemptId(reader.GetGuid(1)),
                reader.IsDBNull(2) ? null : new IngestionAttemptId(reader.GetGuid(2)));
        }

        public ValueTask DisposeAsync() =>
            _ownsDataSource ? DataSource.DisposeAsync() : ValueTask.CompletedTask;
    }

    private sealed record AuthorizedAttempt(
        CollectionJobId JobId,
        IngestionAttemptId AttemptId,
        LeaseGeneration LeaseGeneration,
        FenceToken FenceToken);

    private sealed record EndpointCaptureState(
        FenceToken FenceToken,
        IngestionAttemptId? ActiveAttemptId,
        IngestionAttemptId? LastCurrentCaptureAttemptId);
}
