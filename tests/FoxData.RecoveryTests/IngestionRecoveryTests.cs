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

namespace FoxData.RecoveryTests;

public sealed class IngestionRecoveryTests(RecoveryPostgresFixture postgres)
    : IClassFixture<RecoveryPostgresFixture>
{
    [Fact]
    public async Task ExpiredClaimWithoutAttemptIsSafelyRequeued()
    {
        await using var fixture = await CreateFixtureAsync("no-attempt");

        await fixture.EnqueueAsync("job");
        var claim = await fixture.ClaimAsync();

        await fixture.ExpireJobAsync(claim.Job!.Id);

        var recovered = await fixture.Recovery.RecoverExpiredAsync(
            TestContext.Current.CancellationToken);
        var job = await fixture.Ingestion.GetJobAsync(
            claim.Job.Id,
            TestContext.Current.CancellationToken);

        var item = Assert.Single(recovered.Items);
        Assert.Equal(RecoveryDisposition.RequeuedNoAttempt, item.Disposition);
        Assert.Null(item.AttemptId);
        Assert.Equal(CollectionJobState.Pending, job!.State);
        Assert.Null(job.LeaseOwnerId);
        Assert.Null(job.LeaseExpiresAt);
    }

    [Fact]
    public async Task ExpiredFencedAttemptBeforeExchangeFailsAttemptAndRequeues()
    {
        await using var fixture = await CreateFixtureAsync("before-exchange");

        await fixture.EnqueueAsync("job");
        var claim = await fixture.ClaimAsync();
        var attempt = await fixture.BeginAndFenceAsync(claim, authorize: false);

        await fixture.ExpireJobAsync(claim.Job!.Id);

        var recovered = await fixture.Recovery.RecoverExpiredAsync(
            TestContext.Current.CancellationToken);
        var storedAttempt = await fixture.Ingestion.GetAttemptAsync(
            attempt.AttemptId,
            TestContext.Current.CancellationToken);
        var storedJob = await fixture.Ingestion.GetJobAsync(
            claim.Job.Id,
            TestContext.Current.CancellationToken);
        var activeAttempt = await fixture.ReadActiveAttemptAsync();

        var item = Assert.Single(recovered.Items);
        Assert.Equal(RecoveryDisposition.RequeuedBeforeExchange, item.Disposition);
        Assert.Equal(attempt.AttemptId, item.AttemptId);
        Assert.Equal(IngestionAttemptState.Failed, storedAttempt!.State);
        Assert.Equal("abandoned_before_exchange", storedAttempt.OutcomeCode);
        Assert.NotNull(storedAttempt.RecoveredAt);
        Assert.Equal(CollectionJobState.Pending, storedJob!.State);
        Assert.Null(activeAttempt);
    }

    [Fact]
    public async Task AuthorizedAttemptWithoutFetchBecomesUncertainAndIsNeverReauthorized()
    {
        await using var fixture = await CreateFixtureAsync("uncertain");

        var authorized = await fixture.CreateAuthorizedAttemptAsync("job");
        await fixture.ExpireJobAsync(authorized.JobId);

        var recovered = await fixture.Recovery.RecoverExpiredAsync(
            TestContext.Current.CancellationToken);
        var storedAttempt = await fixture.Ingestion.GetAttemptAsync(
            authorized.AttemptId,
            TestContext.Current.CancellationToken);
        var storedJob = await fixture.Ingestion.GetJobAsync(
            authorized.JobId,
            TestContext.Current.CancellationToken);

        var item = Assert.Single(recovered.Items);
        Assert.Equal(RecoveryDisposition.MarkedUncertain, item.Disposition);
        Assert.Equal(IngestionAttemptState.Uncertain, storedAttempt!.State);
        Assert.Equal("uncertain_exchange", storedAttempt.OutcomeCode);
        Assert.Equal(CollectionJobState.Pending, storedJob!.State);

        var authorization = await fixture.Ingestion.AuthorizeExchangeAsync(
            authorized.AttemptId,
            authorized.WorkerId,
            authorized.LeaseGeneration,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeAuthorizationStatus.AlreadyAuthorized, authorization.Status);
        Assert.False(authorization.MayPerformExchange);

        var repeatedRecovery = await fixture.Recovery.RecoverExpiredAsync(
            TestContext.Current.CancellationToken);

        Assert.Empty(repeatedRecovery.Items);
    }

    [Fact]
    public async Task ExistingFetchRepairsExpiredWorkflowWithoutNewExchange()
    {
        await using var fixture = await CreateFixtureAsync("fetch-repair");

        var authorized = await fixture.CreateAuthorizedAttemptAsync("job");
        await fixture.InsertRawFetchOnlyAsync(authorized.AttemptId);
        await fixture.ExpireJobAsync(authorized.JobId);

        var recovered = await fixture.Recovery.RecoverExpiredAsync(
            TestContext.Current.CancellationToken);
        var storedAttempt = await fixture.Ingestion.GetAttemptAsync(
            authorized.AttemptId,
            TestContext.Current.CancellationToken);
        var storedJob = await fixture.Ingestion.GetJobAsync(
            authorized.JobId,
            TestContext.Current.CancellationToken);

        var item = Assert.Single(recovered.Items);
        Assert.Equal(RecoveryDisposition.RepairedFromFetch, item.Disposition);
        Assert.Equal(IngestionAttemptState.CapturedLate, storedAttempt!.State);
        Assert.NotNull(storedAttempt.RawDurableAt);
        Assert.NotNull(storedAttempt.RecoveredAt);
        Assert.Equal(CollectionJobState.Completed, storedJob!.State);
        Assert.Null(storedJob.LeaseOwnerId);
        Assert.Null(storedJob.LeaseExpiresAt);
    }

    [Fact]
    public async Task UnknownCaptureCommitIsReconciledByStableAttemptId()
    {
        await using var fixture = await CreateFixtureAsync("unknown-commit");

        var authorized = await fixture.CreateAuthorizedAttemptAsync("job");
        var body = new byte[] { 6, 2, 6, 4 };

        _ = await fixture.Evidence.CaptureSourceResponseAsync(
            authorized.AttemptId,
            fixture.EndpointId,
            authorized.LeaseGeneration,
            authorized.FenceToken,
            CreateObservation(body.Length),
            body,
            cancellationToken: TestContext.Current.CancellationToken);

        var reconciled = await fixture.Evidence.CaptureSourceResponseAsync(
            authorized.AttemptId,
            fixture.EndpointId,
            authorized.LeaseGeneration,
            authorized.FenceToken,
            CreateObservation(body.Length),
            body,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStatus.AlreadyCaptured, reconciled.Status);

        var fetch = await fixture.Evidence.GetAttemptEvidenceAsync(
            authorized.AttemptId,
            TestContext.Current.CancellationToken);
        var recovery = await fixture.Recovery.RecoverExpiredAsync(
            TestContext.Current.CancellationToken);

        Assert.NotNull(fetch);
        Assert.Empty(recovery.Items);
    }

    [Fact]
    public async Task LateResponseAfterRecoveryCannotOverwriteNewerFenceAuthority()
    {
        await using var fixture = await CreateFixtureAsync("late-after-recovery");

        var oldAttempt = await fixture.CreateAuthorizedAttemptAsync("job");
        await fixture.ExpireJobAsync(oldAttempt.JobId);

        var recovered = await fixture.Recovery.RecoverExpiredAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(
            RecoveryDisposition.MarkedUncertain,
            Assert.Single(recovered.Items).Disposition);

        var newClaim = await fixture.ClaimAsync();
        var newAttempt = await fixture.BeginAndFenceAsync(newClaim, authorize: true);

        Assert.True(newAttempt.FenceToken.Value > oldAttempt.FenceToken.Value);
        Assert.True(newClaim.Job!.LeaseGeneration.Value > oldAttempt.LeaseGeneration.Value);

        var oldCapture = await fixture.Evidence.CaptureSourceResponseAsync(
            oldAttempt.AttemptId,
            fixture.EndpointId,
            oldAttempt.LeaseGeneration,
            oldAttempt.FenceToken,
            CreateObservation(1),
            new byte[] { 1 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStatus.CapturedLate, oldCapture.Status);

        var beforeCurrent = await fixture.ReadAuthorityAsync();
        Assert.Equal(newAttempt.AttemptId, beforeCurrent.ActiveAttemptId);
        Assert.Null(beforeCurrent.LastCurrentCaptureAttemptId);

        var newCapture = await fixture.Evidence.CaptureSourceResponseAsync(
            newAttempt.AttemptId,
            fixture.EndpointId,
            newAttempt.LeaseGeneration,
            newAttempt.FenceToken,
            CreateObservation(1),
            new byte[] { 2 },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStatus.CapturedCurrent, newCapture.Status);

        var authority = await fixture.ReadAuthorityAsync();
        Assert.Null(authority.ActiveAttemptId);
        Assert.Equal(newAttempt.AttemptId, authority.LastCurrentCaptureAttemptId);
        Assert.Equal(newAttempt.FenceToken, authority.FenceToken);
    }

    private static SourceResponseObservation CreateObservation(long length)
    {
        var now = DateTimeOffset.UtcNow;

        return new SourceResponseObservation(
            now.AddMilliseconds(-2),
            now.AddMilliseconds(-1),
            now,
            "fixture",
            200,
            "application/octet-stream",
            null,
            length,
            null,
            null,
            null,
            2);
    }

    private async Task<RecoveryFixture> CreateFixtureAsync(string scenario)
    {
        await MigrateAsync();
        await ResetAsync();

        var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry = new SourceRegistry(new PostgresSourceRegistryStore(dataSource));

        var source = await registry.RegisterSourceAsync(
            $"recovery-{scenario}",
            $"Recovery {scenario}",
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

        return new RecoveryFixture(
            dataSource,
            new IngestionKernel(new PostgresIngestionKernelStore(dataSource)),
            new IngestionRecovery(
                new PostgresIngestionRecoveryStore(dataSource),
                new IngestionRecoveryLimits(batchSize: 8)),
            new EvidenceKernel(
                new PostgresEvidenceKernelStore(dataSource),
                new EvidenceKernelLimits(maxPayloadBytes: 1024 * 1024)),
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

    private sealed class RecoveryFixture(
        NpgsqlDataSource dataSource,
        IngestionKernel ingestion,
        IngestionRecovery recovery,
        EvidenceKernel evidence,
        EndpointId endpointId) : IAsyncDisposable
    {
        public IngestionKernel Ingestion { get; } = ingestion;

        public IngestionRecovery Recovery { get; } = recovery;

        public EvidenceKernel Evidence { get; } = evidence;

        public EndpointId EndpointId { get; } = endpointId;

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

        public async Task<JobClaimResult> ClaimAsync()
        {
            var result = await Ingestion.ClaimNextAsync(
                WorkerInstanceId.New(),
                TimeSpan.FromMinutes(5),
                TestContext.Current.CancellationToken);

            Assert.True(result.Claimed);
            return result;
        }

        public async Task<AuthorizedAttempt> CreateAuthorizedAttemptAsync(string idempotencyKey)
        {
            await EnqueueAsync(idempotencyKey);
            var claim = await ClaimAsync();
            return await BeginAndFenceAsync(claim, authorize: true);
        }

        public async Task<AuthorizedAttempt> BeginAndFenceAsync(
            JobClaimResult claim,
            bool authorize)
        {
            var workerId = claim.Job!.LeaseOwnerId
                ?? throw new InvalidOperationException("Claimed job has no lease owner.");
            var attemptId = IngestionAttemptId.New();

            var started = await Ingestion.BeginAttemptAsync(
                attemptId,
                claim.Job.Id,
                workerId,
                claim.Job.LeaseGeneration,
                TestContext.Current.CancellationToken);
            Assert.Equal(BeginAttemptStatus.Started, started.Status);

            var fenced = await Ingestion.AcquireEndpointFenceAsync(
                attemptId,
                workerId,
                claim.Job.LeaseGeneration,
                TestContext.Current.CancellationToken);
            Assert.Equal(FenceAcquireStatus.AcquiredNow, fenced.Status);

            if (authorize)
            {
                var authorization = await Ingestion.AuthorizeExchangeAsync(
                    attemptId,
                    workerId,
                    claim.Job.LeaseGeneration,
                    TestContext.Current.CancellationToken);
                Assert.Equal(ExchangeAuthorizationStatus.AuthorizedNow, authorization.Status);
            }

            return new AuthorizedAttempt(
                claim.Job.Id,
                attemptId,
                workerId,
                claim.Job.LeaseGeneration,
                fenced.Attempt!.FenceToken!.Value);
        }

        public async Task ExpireJobAsync(CollectionJobId jobId)
        {
            await using var command = dataSource.CreateCommand(
                """
                UPDATE ingest.collection_jobs
                SET lease_expires_at = transaction_timestamp() - interval '1 second'
                WHERE id = @job_id;
                """);
            command.Parameters.AddWithValue("job_id", jobId.Value);

            Assert.Equal(
                1,
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        public async Task InsertRawFetchOnlyAsync(IngestionAttemptId attemptId)
        {
            await using var command = dataSource.CreateCommand(
                """
                INSERT INTO evidence.fetches
                    (id, attempt_id, endpoint_id, request_started_at, response_started_at,
                     retrieved_at, transport_kind, status_code, duration_ms)
                VALUES
                    (@id, @attempt_id, @endpoint_id,
                     transaction_timestamp(), transaction_timestamp(),
                     transaction_timestamp(), 'fixture', 200, 0);
                """);
            command.Parameters.AddWithValue("id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("attempt_id", attemptId.Value);
            command.Parameters.AddWithValue("endpoint_id", EndpointId.Value);

            Assert.Equal(
                1,
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        public async Task<IngestionAttemptId?> ReadActiveAttemptAsync()
        {
            var authority = await ReadAuthorityAsync();
            return authority.ActiveAttemptId;
        }

        public async Task<EndpointCaptureState> ReadAuthorityAsync()
        {
            await using var command = dataSource.CreateCommand(
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

        public ValueTask DisposeAsync() => dataSource.DisposeAsync();
    }

    private sealed record AuthorizedAttempt(
        CollectionJobId JobId,
        IngestionAttemptId AttemptId,
        WorkerInstanceId WorkerId,
        LeaseGeneration LeaseGeneration,
        FenceToken FenceToken);

    private sealed record EndpointCaptureState(
        FenceToken FenceToken,
        IngestionAttemptId? ActiveAttemptId,
        IngestionAttemptId? LastCurrentCaptureAttemptId);
}
