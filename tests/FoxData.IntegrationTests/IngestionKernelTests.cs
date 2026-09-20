using System.Collections.Concurrent;
using FoxData.Application.Ingestion;
using FoxData.Application.Sources;
using FoxData.Core.Ingestion;
using FoxData.Infrastructure.Ingestion;
using FoxData.Infrastructure.Persistence;
using FoxData.Infrastructure.Sources;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class IngestionKernelTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task EnqueueIsIdempotentAndRejectsConflictingDefinition()
    {
        await using var fixture = await CreateKernelFixtureAsync("enqueue");

        var scheduledFor = DateTimeOffset.UtcNow.AddMinutes(-1);
        var availableAt = scheduledFor;

        var created = await fixture.Kernel.EnqueueAsync(
            fixture.EndpointId,
            "job-1",
            scheduledFor,
            availableAt,
            priority: 5,
            TestContext.Current.CancellationToken);

        var existing = await fixture.Kernel.EnqueueAsync(
            fixture.EndpointId,
            "job-1",
            scheduledFor,
            availableAt,
            priority: 5,
            TestContext.Current.CancellationToken);

        var conflict = await fixture.Kernel.EnqueueAsync(
            fixture.EndpointId,
            "job-1",
            scheduledFor,
            availableAt.AddSeconds(1),
            priority: 5,
            TestContext.Current.CancellationToken);

        Assert.Equal(JobEnqueueStatus.Created, created.Status);
        Assert.Equal(JobEnqueueStatus.Existing, existing.Status);
        Assert.Equal(JobEnqueueStatus.Conflict, conflict.Status);
        Assert.Equal(created.Job.Id, existing.Job.Id);
        Assert.Equal(created.Job.Id, conflict.Job.Id);
    }

    [Fact]
    public async Task ConcurrentDuplicateEnqueueConvergesOnOneJob()
    {
        await using var fixture = await CreateKernelFixtureAsync("enqueue-concurrent");
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => fixture.Kernel.EnqueueAsync(
                fixture.EndpointId,
                "job-concurrent",
                now,
                now,
                priority: 3,
                TestContext.Current.CancellationToken))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Single(
            results,
            result => result.Status is JobEnqueueStatus.Created);
        Assert.Equal(
            7,
            results.Count(result => result.Status is JobEnqueueStatus.Existing));
        Assert.Single(results.Select(result => result.Job.Id).Distinct());
    }

    [Fact]
    public async Task ConcurrentClaimersClaimEachAvailableJobOnce()
    {
        await using var fixture = await CreateKernelFixtureAsync("claim");
        const int jobCount = 32;
        const int workerCount = 8;

        var now = DateTimeOffset.UtcNow.AddMinutes(-1);

        for (var index = 0; index < jobCount; index++)
        {
            var result = await fixture.Kernel.EnqueueAsync(
                fixture.EndpointId,
                $"job-{index:D3}",
                now,
                now,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(JobEnqueueStatus.Created, result.Status);
        }

        var claimed = new ConcurrentBag<(CollectionJobId JobId, LeaseGeneration Generation)>();

        var workers = Enumerable.Range(0, workerCount)
            .Select(async _ =>
            {
                var workerId = WorkerInstanceId.New();

                while (true)
                {
                    var result = await fixture.Kernel.ClaimNextAsync(
                        workerId,
                        TimeSpan.FromMinutes(5),
                        TestContext.Current.CancellationToken);

                    if (!result.Claimed)
                    {
                        return;
                    }

                    claimed.Add((result.Job!.Id, result.Job.LeaseGeneration));
                }
            });

        await Task.WhenAll(workers);

        Assert.Equal(jobCount, claimed.Count);
        Assert.Equal(jobCount, claimed.Select(item => item.JobId).Distinct().Count());
        Assert.All(
            claimed,
            item => Assert.Equal(1L, item.Generation.Value));
    }

    [Fact]
    public async Task StaleWorkerCannotRenewOrReleaseAndReclaimAdvancesGeneration()
    {
        await using var fixture = await CreateKernelFixtureAsync("lease");
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);

        var queued = await fixture.Kernel.EnqueueAsync(
            fixture.EndpointId,
            "job-lease",
            now,
            now,
            cancellationToken: TestContext.Current.CancellationToken);

        var firstWorker = WorkerInstanceId.New();
        var secondWorker = WorkerInstanceId.New();

        var firstClaim = await fixture.Kernel.ClaimNextAsync(
            firstWorker,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        Assert.True(firstClaim.Claimed);
        Assert.Equal(queued.Job.Id, firstClaim.Job!.Id);

        var staleRenew = await fixture.Kernel.RenewLeaseAsync(
            firstClaim.Job.Id,
            secondWorker,
            firstClaim.Job.LeaseGeneration,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        var staleRelease = await fixture.Kernel.ReleaseLeaseAsync(
            firstClaim.Job.Id,
            secondWorker,
            firstClaim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        Assert.Equal(LeaseRenewalStatus.Lost, staleRenew.Status);
        Assert.Equal(LeaseReleaseStatus.Lost, staleRelease.Status);

        var renewal = await fixture.Kernel.RenewLeaseAsync(
            firstClaim.Job.Id,
            firstWorker,
            firstClaim.Job.LeaseGeneration,
            TimeSpan.FromMinutes(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(LeaseRenewalStatus.Renewed, renewal.Status);

        var release = await fixture.Kernel.ReleaseLeaseAsync(
            firstClaim.Job.Id,
            firstWorker,
            firstClaim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        Assert.Equal(LeaseReleaseStatus.Released, release.Status);

        var secondClaim = await fixture.Kernel.ClaimNextAsync(
            secondWorker,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        Assert.True(secondClaim.Claimed);
        Assert.Equal(firstClaim.Job.Id, secondClaim.Job!.Id);
        Assert.Equal(2L, secondClaim.Job.LeaseGeneration.Value);
    }

    [Fact]
    public async Task BeginAttemptAndFenceAreIdempotentAndAuthorizationIsOneWay()
    {
        await using var fixture = await CreateKernelFixtureAsync("attempt");
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);

        await fixture.Kernel.EnqueueAsync(
            fixture.EndpointId,
            "job-attempt",
            now,
            now,
            cancellationToken: TestContext.Current.CancellationToken);

        var worker = WorkerInstanceId.New();
        var claim = await fixture.Kernel.ClaimNextAsync(
            worker,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        var attemptId = IngestionAttemptId.New();

        var started = await fixture.Kernel.BeginAttemptAsync(
            attemptId,
            claim.Job!.Id,
            worker,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        var retriedStart = await fixture.Kernel.BeginAttemptAsync(
            attemptId,
            claim.Job.Id,
            worker,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        Assert.Equal(BeginAttemptStatus.Started, started.Status);
        Assert.Equal(BeginAttemptStatus.Existing, retriedStart.Status);
        Assert.Equal(1, started.Attempt!.AttemptNumber);

        var fenced = await fixture.Kernel.AcquireEndpointFenceAsync(
            attemptId,
            worker,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        var fencedAgain = await fixture.Kernel.AcquireEndpointFenceAsync(
            attemptId,
            worker,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        Assert.Equal(FenceAcquireStatus.AcquiredNow, fenced.Status);
        Assert.Equal(FenceAcquireStatus.AlreadyAcquired, fencedAgain.Status);
        Assert.Equal(1L, fenced.Attempt!.FenceToken!.Value.Value);
        Assert.Equal(fenced.Attempt.FenceToken, fencedAgain.Attempt!.FenceToken);

        var authorized = await fixture.Kernel.AuthorizeExchangeAsync(
            attemptId,
            worker,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        var authorizedAgain = await fixture.Kernel.AuthorizeExchangeAsync(
            attemptId,
            worker,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeAuthorizationStatus.AuthorizedNow, authorized.Status);
        Assert.True(authorized.MayPerformExchange);
        Assert.Equal(ExchangeAuthorizationStatus.AlreadyAuthorized, authorizedAgain.Status);
        Assert.False(authorizedAgain.MayPerformExchange);
    }

    [Fact]
    public async Task NewerFenceMakesOlderAttemptStale()
    {
        await using var fixture = await CreateKernelFixtureAsync("stale-fence");
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);

        await fixture.Kernel.EnqueueAsync(
            fixture.EndpointId,
            "job-first",
            now,
            now,
            cancellationToken: TestContext.Current.CancellationToken);

        await fixture.Kernel.EnqueueAsync(
            fixture.EndpointId,
            "job-second",
            now,
            now,
            cancellationToken: TestContext.Current.CancellationToken);

        var firstWorker = WorkerInstanceId.New();
        var secondWorker = WorkerInstanceId.New();

        var firstClaim = await fixture.Kernel.ClaimNextAsync(
            firstWorker,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        var secondClaim = await fixture.Kernel.ClaimNextAsync(
            secondWorker,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        var firstAttempt = IngestionAttemptId.New();
        var secondAttempt = IngestionAttemptId.New();

        await fixture.Kernel.BeginAttemptAsync(
            firstAttempt,
            firstClaim.Job!.Id,
            firstWorker,
            firstClaim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        await fixture.Kernel.BeginAttemptAsync(
            secondAttempt,
            secondClaim.Job!.Id,
            secondWorker,
            secondClaim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        var firstFence = await fixture.Kernel.AcquireEndpointFenceAsync(
            firstAttempt,
            firstWorker,
            firstClaim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        var secondFence = await fixture.Kernel.AcquireEndpointFenceAsync(
            secondAttempt,
            secondWorker,
            secondClaim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        Assert.Equal(1L, firstFence.Attempt!.FenceToken!.Value.Value);
        Assert.Equal(2L, secondFence.Attempt!.FenceToken!.Value.Value);

        var staleAuthorization = await fixture.Kernel.AuthorizeExchangeAsync(
            firstAttempt,
            firstWorker,
            firstClaim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeAuthorizationStatus.StaleFence, staleAuthorization.Status);

        var currentAuthorization = await fixture.Kernel.AuthorizeExchangeAsync(
            secondAttempt,
            secondWorker,
            secondClaim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeAuthorizationStatus.AuthorizedNow, currentAuthorization.Status);
        Assert.True(currentAuthorization.MayPerformExchange);
    }

    [Fact]
    public async Task AuthorizationUsesActualDatabaseTimeAfterWaitingForJobLock()
    {
        await using var fixture = await CreateKernelFixtureAsync("lease-clock");
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);

        await fixture.Kernel.EnqueueAsync(
            fixture.EndpointId,
            "job-lease-clock",
            now,
            now,
            cancellationToken: TestContext.Current.CancellationToken);

        var worker = WorkerInstanceId.New();
        var claim = await fixture.Kernel.ClaimNextAsync(
            worker,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        var attemptId = IngestionAttemptId.New();

        var started = await fixture.Kernel.BeginAttemptAsync(
            attemptId,
            claim.Job!.Id,
            worker,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);
        Assert.Equal(BeginAttemptStatus.Started, started.Status);

        var fenced = await fixture.Kernel.AcquireEndpointFenceAsync(
            attemptId,
            worker,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);
        Assert.Equal(FenceAcquireStatus.AcquiredNow, fenced.Status);

        await using (var shorten = fixture.DataSource.CreateCommand(
            """
            UPDATE ingest.collection_jobs
            SET lease_expires_at = clock_timestamp() + interval '2 seconds'
            WHERE id = @job_id;
            """))
        {
            shorten.Parameters.AddWithValue("job_id", claim.Job.Id.Value);
            Assert.Equal(
                1,
                await shorten.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        await using var blockerConnection =
            await fixture.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var blockerTransaction =
            await blockerConnection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var block = blockerConnection.CreateCommand())
        {
            block.Transaction = blockerTransaction;
            block.CommandText =
                """
                SELECT id
                FROM ingest.collection_jobs
                WHERE id = @job_id
                FOR UPDATE;
                """;
            block.Parameters.AddWithValue("job_id", claim.Job.Id.Value);
            _ = await block.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        var authorizationTask = fixture.Kernel.AuthorizeExchangeAsync(
            attemptId,
            worker,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        await WaitUntilAuthorizationIsBlockedAsync(
            fixture.DataSource,
            TestContext.Current.CancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(2.2), TestContext.Current.CancellationToken);
        await blockerTransaction.CommitAsync(TestContext.Current.CancellationToken);

        var authorization = await authorizationTask;

        Assert.Equal(ExchangeAuthorizationStatus.LeaseLost, authorization.Status);
        Assert.False(authorization.MayPerformExchange);
    }

    private static async Task WaitUntilAuthorizationIsBlockedAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = dataSource.CreateCommand(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND wait_event_type = 'Lock'
                      AND state = 'active'
                      AND query LIKE '%FROM ingest.collection_jobs%'
                      AND query LIKE '%FOR UPDATE%'
                );
                """);

            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        throw new TimeoutException(
            "Authorization did not reach the expected blocked job-lock state.");
    }

    [Fact]
    public async Task PreExchangeFailureCanBeDeferredImmediatelyAndIdempotently()
    {
        await using var fixture = await CreateKernelFixtureAsync("defer-before-exchange");
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);

        await fixture.Kernel.EnqueueAsync(
            fixture.EndpointId,
            "job-defer-before",
            now,
            now,
            cancellationToken: TestContext.Current.CancellationToken);

        var worker = WorkerInstanceId.New();
        var claim = await fixture.Kernel.ClaimNextAsync(
            worker,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.True(claim.Claimed);

        var attemptId = IngestionAttemptId.New();
        var started = await fixture.Kernel.BeginAttemptAsync(
            attemptId,
            claim.Job!.Id,
            worker,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);
        Assert.Equal(BeginAttemptStatus.Started, started.Status);

        var fenced = await fixture.Kernel.AcquireEndpointFenceAsync(
            attemptId,
            worker,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);
        Assert.Equal(FenceAcquireStatus.AcquiredNow, fenced.Status);

        var retryAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var deferred = await fixture.Kernel.DeferBeforeExchangeAsync(
            attemptId,
            worker,
            claim.Job.LeaseGeneration,
            retryAt,
            "request_build",
            "invalid_configuration",
            TestContext.Current.CancellationToken);

        Assert.Equal(AttemptDeferralStatus.DeferredNow, deferred.Status);
        Assert.Equal(IngestionAttemptState.Failed, deferred.Attempt!.State);
        Assert.Equal("abandoned_before_exchange", deferred.Attempt.OutcomeCode);
        Assert.Equal("request_build", deferred.Attempt.ErrorClass);
        Assert.Equal("invalid_configuration", deferred.Attempt.ErrorCode);
        Assert.Equal(CollectionJobState.Pending, deferred.Job!.State);
        Assert.Null(deferred.Job.LeaseOwnerId);
        Assert.Null(deferred.Job.LeaseExpiresAt);
        Assert.Equal(
            NormalizeTimestamp(retryAt),
            deferred.Job.AvailableAt);
        Assert.Null(await fixture.ReadActiveAttemptAsync());

        var repeated = await fixture.Kernel.DeferBeforeExchangeAsync(
            attemptId,
            worker,
            claim.Job.LeaseGeneration,
            retryAt,
            "request_build",
            "invalid_configuration",
            TestContext.Current.CancellationToken);

        Assert.Equal(AttemptDeferralStatus.AlreadyDeferred, repeated.Status);

        var earlyClaim = await fixture.Kernel.ClaimNextAsync(
            WorkerInstanceId.New(),
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        Assert.False(earlyClaim.Claimed);
    }

    [Fact]
    public async Task AuthorizedFailureCanBeMarkedUncertainAndDeferredWithoutReplayPermission()
    {
        await using var fixture = await CreateKernelFixtureAsync("defer-uncertain");
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);

        await fixture.Kernel.EnqueueAsync(
            fixture.EndpointId,
            "job-defer-uncertain",
            now,
            now,
            cancellationToken: TestContext.Current.CancellationToken);

        var worker = WorkerInstanceId.New();
        var claim = await fixture.Kernel.ClaimNextAsync(
            worker,
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.True(claim.Claimed);

        var attemptId = IngestionAttemptId.New();
        _ = await fixture.Kernel.BeginAttemptAsync(
            attemptId,
            claim.Job!.Id,
            worker,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);
        _ = await fixture.Kernel.AcquireEndpointFenceAsync(
            attemptId,
            worker,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        var authorized = await fixture.Kernel.AuthorizeExchangeAsync(
            attemptId,
            worker,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);
        Assert.Equal(ExchangeAuthorizationStatus.AuthorizedNow, authorized.Status);

        var retryAt = DateTimeOffset.UtcNow.AddSeconds(30);
        var deferred = await fixture.Kernel.DeferUncertainExchangeAsync(
            attemptId,
            worker,
            claim.Job.LeaseGeneration,
            retryAt,
            "transport",
            "connection_reset",
            TestContext.Current.CancellationToken);

        Assert.Equal(AttemptDeferralStatus.DeferredNow, deferred.Status);
        Assert.Equal(IngestionAttemptState.Uncertain, deferred.Attempt!.State);
        Assert.Equal("uncertain_exchange", deferred.Attempt.OutcomeCode);
        Assert.Equal(CollectionJobState.Pending, deferred.Job!.State);
        Assert.Equal(
            NormalizeTimestamp(retryAt),
            deferred.Job.AvailableAt);
        Assert.Null(await fixture.ReadActiveAttemptAsync());

        var authorizationRetry = await fixture.Kernel.AuthorizeExchangeAsync(
            attemptId,
            worker,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ExchangeAuthorizationStatus.AlreadyAuthorized,
            authorizationRetry.Status);
        Assert.False(authorizationRetry.MayPerformExchange);

        var repeated = await fixture.Kernel.DeferUncertainExchangeAsync(
            attemptId,
            worker,
            claim.Job.LeaseGeneration,
            retryAt,
            "transport",
            "connection_reset",
            TestContext.Current.CancellationToken);

        Assert.Equal(AttemptDeferralStatus.AlreadyDeferred, repeated.Status);
    }

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset value)
    {
        const long ticksPerMicrosecond = 10;
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(
            utc.Ticks - (utc.Ticks % ticksPerMicrosecond),
            TimeSpan.Zero);
    }

    private async Task<KernelFixture> CreateKernelFixtureAsync(string scenario)
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

        return new KernelFixture(
            dataSource,
            new IngestionKernel(new PostgresIngestionKernelStore(dataSource)),
            endpoint.Resource.Id);
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

    private async Task MigrateAsync()
    {
        var options = new DbContextOptionsBuilder<FoxDataDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var context = new FoxDataDbContext(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    private sealed class KernelFixture(
        NpgsqlDataSource dataSource,
        IngestionKernel kernel,
        FoxData.Core.Sources.EndpointId endpointId) : IAsyncDisposable
    {
        public NpgsqlDataSource DataSource { get; } = dataSource;

        public IngestionKernel Kernel { get; } = kernel;

        public FoxData.Core.Sources.EndpointId EndpointId { get; } = endpointId;

        public async Task<IngestionAttemptId?> ReadActiveAttemptAsync()
        {
            await using var command = DataSource.CreateCommand(
                """
                SELECT active_attempt_id
                FROM ingest.endpoint_state
                WHERE endpoint_id = @endpoint_id;
                """);
            command.Parameters.AddWithValue("endpoint_id", EndpointId.Value);

            var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            return value is Guid attemptId
                ? new IngestionAttemptId(attemptId)
                : null;
        }

        public ValueTask DisposeAsync() => DataSource.DisposeAsync();
    }
}
