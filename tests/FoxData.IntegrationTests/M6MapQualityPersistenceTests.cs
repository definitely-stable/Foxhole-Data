using System.Text;
using FoxData.Application.Canonical;
using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
using FoxData.Application.Quality;
using FoxData.Application.Sources;
using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Runtime;
using FoxData.Infrastructure.Canonical;
using FoxData.Infrastructure.Evidence;
using FoxData.Infrastructure.Ingestion;
using FoxData.Infrastructure.Persistence;
using FoxData.Infrastructure.Sources;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class M6MapQualityPersistenceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task AcceptedQualityAtomicallyCreatesObservationAndEnrichesRegion()
    {
        await using var fixture = await CreateFixtureAsync();
        var snapshot = await fixture.CreateSnapshotAsync(
            "accepted",
            fixture.Start,
            sourceRegionId: 7,
            rawTeamId: "WARDENS");

        var write = fixture.CreateQualityWrite(
            snapshot,
            MapQualityDecision.Accepted,
            baseline: null);

        var first = await fixture.Kernel.RecordAsync(
            write,
            TestContext.Current.CancellationToken);
        var replay = await fixture.Kernel.RecordAsync(
            write with
            {
                StartedAt = write.StartedAt.AddSeconds(1),
                CompletedAt = write.CompletedAt.AddSeconds(1),
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(first.Observation);
        Assert.Equal(first.Run.Id, replay.Run.Id);
        Assert.Equal(first.Observation!.Id, replay.Observation!.Id);
        Assert.Equal(
            7,
            await fixture.ReadWarRegionSourceRegionIdAsync());
        Assert.Equal(
            7,
            await fixture.ReadWarRegionSourceRegionIdAsync());
        Assert.Equal(
            snapshot.Snapshot.RepresentationFetchId,
            first.Observation.ValidationFetchId);
        Assert.Equal(
            fixture.Start,
            first.Observation.ObservedAt);
        Assert.Equal(1L, await fixture.CountAsync(
            "quality.map_quality_runs"));
        Assert.Equal(1L, await fixture.CountAsync(
            "runtime.map_observations"));
        Assert.Equal(1L, await fixture.CountAsync(
            "quality.map_quality_findings"));
    }

    [Fact]
    public async Task SuspectQualityCreatesNoObservationAndDoesNotEnrichRegion()
    {
        await using var fixture = await CreateFixtureAsync();
        var snapshot = await fixture.CreateSnapshotAsync(
            "suspect",
            fixture.Start,
            sourceRegionId: 9,
            rawTeamId: "FUTURE_TEAM");

        var result = await fixture.Kernel.RecordAsync(
            fixture.CreateQualityWrite(
                snapshot,
                MapQualityDecision.Suspect,
                baseline: null),
            TestContext.Current.CancellationToken);

        Assert.Null(result.Observation);
        Assert.Null(
            await fixture.ReadWarRegionSourceRegionIdAsync());
        Assert.Equal(1L, await fixture.CountAsync(
            "quality.map_quality_runs"));
        Assert.Equal(0L, await fixture.CountAsync(
            "runtime.map_observations"));
    }

    [Fact]
    public async Task StoreRequiresLatestAcceptedBaseline()
    {
        await using var fixture = await CreateFixtureAsync();
        var firstSnapshot = await fixture.CreateSnapshotAsync(
            "baseline-a",
            fixture.Start,
            sourceRegionId: null,
            rawTeamId: "WARDENS");
        var first = await fixture.Kernel.RecordAsync(
            fixture.CreateQualityWrite(
                firstSnapshot,
                MapQualityDecision.Accepted,
                baseline: null),
            TestContext.Current.CancellationToken);

        var secondSnapshot = await fixture.CreateSnapshotAsync(
            "baseline-b",
            fixture.Start.AddMinutes(1),
            sourceRegionId: null,
            rawTeamId: "COLONIALS");

        await Assert.ThrowsAsync<CanonicalStateIntegrityException>(
            () => fixture.Kernel.RecordAsync(
                fixture.CreateQualityWrite(
                    secondSnapshot,
                    MapQualityDecision.Accepted,
                    baseline: null),
                TestContext.Current.CancellationToken));

        var second = await fixture.Kernel.RecordAsync(
            fixture.CreateQualityWrite(
                secondSnapshot,
                MapQualityDecision.Accepted,
                first.Observation!.Id),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            first.Observation.Id,
            second.Run.BaselineMapObservationId);
        Assert.Equal(2L, await fixture.CountAsync(
            "runtime.map_observations"));
    }

    [Fact]
    public async Task DatabaseRejectsFindingBoundToDifferentSnapshotOccurrence()
    {
        await using var fixture = await CreateFixtureAsync();
        var first = await fixture.CreateSnapshotAsync(
            "finding-a",
            fixture.Start,
            null,
            "WARDENS");
        var second = await fixture.CreateSnapshotAsync(
            "finding-b",
            fixture.Start.AddMinutes(1),
            null,
            "COLONIALS");

        var qualityRunId = Guid.CreateVersion7();
        await fixture.InsertQualityRunAsync(
            qualityRunId,
            first.Snapshot.Id.Value,
            "suspect");

        await using var command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO quality.map_quality_findings
                (id, quality_run_id, rule_key, rule_version,
                 configuration_version, effect,
                 map_item_occurrence_id, input_metrics)
            VALUES
                (@id, @quality_run_id,
                 'coordinate.valid', 'coordinate.valid@1',
                 'coordinate.valid-config@1', 'quarantined',
                 @occurrence_id, '{}'::jsonb);
            """);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue(
            "quality_run_id",
            qualityRunId);
        command.Parameters.AddWithValue(
            "occurrence_id",
            second.Items[0].Id.Value);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            exception.SqlState);
        Assert.Equal(
            "ck_map_quality_findings_snapshot_binding",
            exception.ConstraintName);
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        await MigrateAsync();
        await ResetAsync();

        var dataSource =
            NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry = new SourceRegistry(
            new PostgresSourceRegistryStore(dataSource));
        var source = await registry.RegisterSourceAsync(
            "official-war-api",
            "Official War API",
            TestContext.Current.CancellationToken);
        var shard = await registry.RegisterShardAsync(
            source.Resource.Id,
            "live-1",
            "Live 1",
            "live",
            TestContext.Current.CancellationToken);
        var endpoint = await registry.RegisterEndpointAsync(
            shard.Resource.Id,
            "dynamic-map-state",
            "map-dynamic/DeadLandsHex",
            TestContext.Current.CancellationToken);

        var start = new DateTimeOffset(
            2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        var warId = WarId.New();
        var regionId = RegionId.New();
        var warRegionId = WarRegionId.New();

        await using (var command = dataSource.CreateCommand(
            """
            INSERT INTO runtime.wars
                (id, shard_id, source_war_id, war_number,
                 first_observed_at, last_observed_at)
            VALUES
                (@war_id, @shard_id, 'm6-quality-war', 1,
                 @at, @at);

            INSERT INTO runtime.regions
                (id, canonical_key, display_name)
            VALUES
                (@region_id,
                 'official-war-api/map/DeadLandsHex',
                 'DeadLandsHex');

            INSERT INTO runtime.war_regions
                (id, war_id, region_id, source_map_name,
                 first_seen_at, last_seen_at)
            VALUES
                (@war_region_id, @war_id, @region_id,
                 'DeadLandsHex', @at, @at);
            """))
        {
            command.Parameters.AddWithValue(
                "war_id",
                warId.Value);
            command.Parameters.AddWithValue(
                "shard_id",
                shard.Resource.Id.Value);
            command.Parameters.AddWithValue(
                "region_id",
                regionId.Value);
            command.Parameters.AddWithValue(
                "war_region_id",
                warRegionId.Value);
            command.Parameters.AddWithValue("at", start);

            await command.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
        }

        return new Fixture(
            dataSource,
            registry,
            source.Resource.Id,
            endpoint.Resource.Id,
            warRegionId,
            start,
            new IngestionKernel(
                new PostgresIngestionKernelStore(dataSource)),
            new EvidenceKernel(
                new PostgresEvidenceKernelStore(dataSource)),
            new PostgresSourceParseRunStore(dataSource),
            new MapSnapshotKernel(
                new PostgresMapSnapshotStore(dataSource)),
            new MapQualityKernel(
                new PostgresMapQualityStore(dataSource)));
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
                runtime.regions,
                sources.sources
            RESTART IDENTITY CASCADE;
            """);

        await command.ExecuteNonQueryAsync(
            TestContext.Current.CancellationToken);
    }

    private sealed class Fixture(
        NpgsqlDataSource dataSource,
        SourceRegistry registry,
        FoxData.Core.Sources.SourceId sourceId,
        FoxData.Core.Sources.EndpointId endpointId,
        WarRegionId warRegionId,
        DateTimeOffset start,
        IngestionKernel ingestion,
        EvidenceKernel evidence,
        ISourceParseRunStore parseRuns,
        MapSnapshotKernel snapshots,
        MapQualityKernel kernel)
        : IAsyncDisposable
    {
        private int _sequence;
        private readonly Dictionary<MapSnapshotId, DateTimeOffset>
            _retrievedAtBySnapshot = [];

        public NpgsqlDataSource DataSource { get; } = dataSource;
        public WarRegionId WarRegionId { get; } = warRegionId;
        public DateTimeOffset Start { get; } = start;
        public MapQualityKernel Kernel { get; } = kernel;

        public async Task<MapSnapshotResult> CreateSnapshotAsync(
            string key,
            DateTimeOffset retrievedAt,
            int? sourceRegionId,
            string rawTeamId)
        {
            _sequence++;
            var body = Encoding.UTF8.GetBytes(
                $"{{\"regionId\":{(sourceRegionId?.ToString() ?? "null")},\"mapItems\":[{{\"teamId\":\"{rawTeamId}\",\"iconType\":20,\"x\":0.5,\"y\":0.5,\"flags\":0}}],\"mapTextItems\":[]}}");

            var capture = await CaptureAsync(
                $"{key}-{_sequence}",
                retrievedAt,
                body);

            var parseStartedAt = retrievedAt.AddMilliseconds(1);
            var parse = await parseRuns.RecordAsync(
                new SourceParseRunWrite(
                    capture.Fetch!.Id,
                    "dynamic-map-state",
                    "warapi-adapter@1",
                    "warapi-parser@1",
                    "json-shape@1",
                    $"shape-{key}",
                    "parsed",
                    0,
                    0,
                    null,
                    parseStartedAt,
                    parseStartedAt.AddMilliseconds(1),
                    SourceVersion: _sequence,
                    SourceLastUpdated: null,
                    DecodedByteLength: body.LongLength),
                TestContext.Current.CancellationToken);

            var snapshot = await snapshots.RecordAcceptedAsync(
                new MapSnapshotWrite(
                    parse.Id,
                    "warapi-dynamic-map-normalizer@1",
                    "dynamic-map-state",
                    "map-dynamic/DeadLandsHex",
                    retrievedAt.AddMilliseconds(2),
                    retrievedAt.AddMilliseconds(3),
                    capture.Fetch.Id,
                    MapSnapshotKind.Dynamic,
                    "DeadLandsHex",
                    sourceRegionId,
                    0,
                    _sequence,
                    null,
                    null,
                    true,
                    true,
                    [
                        new MapItemOccurrenceCandidate(
                            0,
                            rawTeamId,
                            20,
                            0.5,
                            0.5,
                            0,
                            null),
                    ],
                    []),
                TestContext.Current.CancellationToken);

            _retrievedAtBySnapshot[snapshot.Snapshot.Id] =
                retrievedAt;
            return snapshot;
        }

        public MapQualityWrite CreateQualityWrite(
            MapSnapshotResult snapshot,
            MapQualityDecision decision,
            MapObservationId? baseline)
        {
            var observedAt =
                _retrievedAtBySnapshot[snapshot.Snapshot.Id];

            return new MapQualityWrite(
                snapshot.Snapshot.Id,
                WarRegionId,
                snapshot.Snapshot.RepresentationFetchId,
                "warapi-map-taxonomy@1",
                "warapi-map-quality@1",
                baseline,
                decision,
                Start.AddMinutes(10),
                Start.AddMinutes(10).AddMilliseconds(1),
                snapshot.Snapshot.Kind,
                observedAt,
                snapshot.Snapshot.SourceUpdatedAt,
                [
                    new MapQualityFindingCandidate(
                        "taxonomy.unknown-team",
                        "taxonomy.unknown-team@1",
                        "taxonomy.unknown-team-config@1",
                        "informational",
                        snapshot.Items[0].Id,
                        null,
                        "test_finding",
                        """{"count":1}"""),
                ]);
        }

        public async Task InsertQualityRunAsync(
            Guid qualityRunId,
            Guid snapshotId,
            string decision)
        {
            await using var command = DataSource.CreateCommand(
                """
                INSERT INTO quality.map_quality_runs
                    (id, map_snapshot_id, war_region_id,
                     validation_fetch_id, taxonomy_version,
                     quality_policy_version, decision,
                     started_at, completed_at)
                SELECT
                    @id, @map_snapshot_id, @war_region_id,
                    representation_fetch_id,
                    'warapi-map-taxonomy@1',
                    'warapi-map-quality@1',
                    @decision, @at, @at
                FROM evidence.map_snapshots
                WHERE id = @map_snapshot_id;
                """);
            command.Parameters.AddWithValue("id", qualityRunId);
            command.Parameters.AddWithValue(
                "map_snapshot_id",
                snapshotId);
            command.Parameters.AddWithValue(
                "war_region_id",
                WarRegionId.Value);
            command.Parameters.AddWithValue("decision", decision);
            command.Parameters.AddWithValue("at", Start);

            Assert.Equal(
                1,
                await command.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken));
        }

        public async Task<long> CountAsync(string table)
        {
            await using var command =
                DataSource.CreateCommand(
                    $"SELECT COUNT(*) FROM {table};");

            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        private async Task<CaptureResult> CaptureAsync(
            string idempotencyKey,
            DateTimeOffset retrievedAt,
            byte[] body)
        {
            var scheduledAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            var queued = await ingestion.EnqueueAsync(
                endpointId,
                idempotencyKey,
                scheduledAt,
                scheduledAt,
                cancellationToken:
                    TestContext.Current.CancellationToken);
            Assert.Equal(JobEnqueueStatus.Created, queued.Status);

            var workerId = WorkerInstanceId.New();
            var claim = await ingestion.ClaimNextAsync(
                workerId,
                TimeSpan.FromMinutes(5),
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

            return await evidence.CaptureSourceResponseAsync(
                attemptId,
                endpointId,
                claim.Job.LeaseGeneration,
                fenced.Attempt!.FenceToken!.Value,
                new SourceResponseObservation(
                    retrievedAt.AddMilliseconds(-2),
                    retrievedAt.AddMilliseconds(-1),
                    retrievedAt,
                    "war-api",
                    200,
                    "application/json",
                    null,
                    body.LongLength,
                    null,
                    "max-age=60",
                    retrievedAt.AddMinutes(1),
                    2),
                body,
                cancellationToken:
                    TestContext.Current.CancellationToken);
        }

        public ValueTask DisposeAsync() =>
            DataSource.DisposeAsync();
    }
}
