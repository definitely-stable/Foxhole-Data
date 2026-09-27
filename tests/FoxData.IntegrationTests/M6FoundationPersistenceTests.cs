using FoxData.Application.Canonical;
using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class M6FoundationPersistenceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task M6MigrationRoundTripsFromM5CoverageBoundary()
    {
        await MigrateAsync();

        Assert.True(await TableExistsAsync(
            "evidence",
            "map_snapshots"));
        Assert.True(await TableExistsAsync(
            "quality",
            "map_quality_runs"));
        Assert.True(await TableExistsAsync(
            "runtime",
            "map_observations"));

        var options = new DbContextOptionsBuilder<FoxDataDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using (var context = new FoxDataDbContext(options))
        {
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(
                "20260927140000_M5CoverageRecovery",
                TestContext.Current.CancellationToken);
        }

        Assert.False(await TableExistsAsync(
            "evidence",
            "map_snapshots"));
        Assert.False(await TableExistsAsync(
            "quality",
            "map_quality_runs"));
        Assert.False(await TableExistsAsync(
            "runtime",
            "map_observations"));

        await MigrateAsync();

        Assert.True(await TableExistsAsync(
            "evidence",
            "map_item_occurrences"));
        Assert.True(await TableExistsAsync(
            "quality",
            "map_quality_findings"));
        Assert.True(await TableExistsAsync(
            "runtime",
            "map_observations"));
    }

    [Fact]
    public async Task SnapshotPreservesDuplicateOccurrencesAndCanBeReusedAcrossBindings()
    {
        await using var fixture = await CreateFixtureAsync();

        var snapshotId = Guid.CreateVersion7();
        await fixture.InsertSnapshotAsync(snapshotId);

        var duplicate = new
        {
            RawTeamId = "WARDENS",
            RawIconType = 56,
            X = 0.5d,
            Y = 0.25d,
            RawFlags = 0,
            RawViewDirection = 0,
        };

        await fixture.InsertItemOccurrenceAsync(
            snapshotId,
            Guid.CreateVersion7(),
            0,
            duplicate.RawTeamId,
            duplicate.RawIconType,
            duplicate.X,
            duplicate.Y,
            duplicate.RawFlags,
            duplicate.RawViewDirection);
        await fixture.InsertItemOccurrenceAsync(
            snapshotId,
            Guid.CreateVersion7(),
            1,
            duplicate.RawTeamId,
            duplicate.RawIconType,
            duplicate.X,
            duplicate.Y,
            duplicate.RawFlags,
            duplicate.RawViewDirection);

        Assert.Equal(
            2L,
            await fixture.CountAsync(
                "evidence.map_item_occurrences"));

        var duplicateOrdinalException =
            await Assert.ThrowsAsync<PostgresException>(
                () => fixture.InsertItemOccurrenceAsync(
                    snapshotId,
                    Guid.CreateVersion7(),
                    1,
                    "COLONIALS",
                    97,
                    0.8d,
                    0.8d,
                    32,
                    90));

        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            duplicateOrdinalException.SqlState);
        Assert.Equal(
            "ux_map_item_occurrences_snapshot_ordinal",
            duplicateOrdinalException.ConstraintName);

        var firstQualityRun = Guid.CreateVersion7();
        var firstObservation = Guid.CreateVersion7();
        await fixture.InsertAcceptedBindingAsync(
            firstQualityRun,
            firstObservation,
            snapshotId,
            fixture.FirstWarRegionId,
            fixture.RepresentationFetchId,
            fixture.RepresentationRetrievedAt);

        var validationFetchId =
            await fixture.CreateValidation304Async(
                fixture.RepresentationFetchId,
                fixture.RepresentationRetrievedAt.AddMinutes(10));

        var secondQualityRun = Guid.CreateVersion7();
        var secondObservation = Guid.CreateVersion7();
        await fixture.InsertAcceptedBindingAsync(
            secondQualityRun,
            secondObservation,
            snapshotId,
            fixture.SecondWarRegionId,
            validationFetchId.Value,
            fixture.RepresentationRetrievedAt.AddMinutes(10));

        Assert.Equal(
            1L,
            await fixture.CountAsync("evidence.map_snapshots"));
        Assert.Equal(
            2L,
            await fixture.CountAsync(
                "evidence.map_item_occurrences"));
        Assert.Equal(
            2L,
            await fixture.CountAsync("quality.map_quality_runs"));
        Assert.Equal(
            2L,
            await fixture.CountAsync("runtime.map_observations"));

        await using var command = fixture.DataSource.CreateCommand(
            """
            SELECT COUNT(DISTINCT map_snapshot_id)
            FROM runtime.map_observations;
            """);

        Assert.Equal(
            1L,
            (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task QualityAndSnapshotShapeConstraintsFailClosed()
    {
        await using var fixture = await CreateFixtureAsync();

        await using var invalidSnapshot =
            fixture.DataSource.CreateCommand(
                """
                INSERT INTO evidence.map_snapshots
                    (id, normalization_run_id, source_parse_run_id,
                     representation_fetch_id, capability_kind,
                     source_map_name, item_count, text_item_count)
                VALUES
                    (@id, @normalization_run_id, @source_parse_run_id,
                     @representation_fetch_id, 'merged',
                     'DeadLandsHex', -1, 0);
                """);
        invalidSnapshot.Parameters.AddWithValue(
            "id",
            Guid.CreateVersion7());
        invalidSnapshot.Parameters.AddWithValue(
            "normalization_run_id",
            fixture.NormalizationRunId);
        invalidSnapshot.Parameters.AddWithValue(
            "source_parse_run_id",
            fixture.SourceParseRunId);
        invalidSnapshot.Parameters.AddWithValue(
            "representation_fetch_id",
            fixture.RepresentationFetchId);

        var snapshotException =
            await Assert.ThrowsAsync<PostgresException>(
                () => invalidSnapshot.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken));

        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            snapshotException.SqlState);

        var snapshotId = Guid.CreateVersion7();
        await fixture.InsertSnapshotAsync(snapshotId);

        await using var invalidQuality =
            fixture.DataSource.CreateCommand(
                """
                INSERT INTO quality.map_quality_runs
                    (id, map_snapshot_id, war_region_id,
                     validation_fetch_id, taxonomy_version,
                     quality_policy_version, decision,
                     started_at, completed_at)
                VALUES
                    (@id, @map_snapshot_id, @war_region_id,
                     @validation_fetch_id, 'taxonomy@1',
                     'quality@1', 'accepted_maybe',
                     @started_at, @completed_at);
                """);
        invalidQuality.Parameters.AddWithValue(
            "id",
            Guid.CreateVersion7());
        invalidQuality.Parameters.AddWithValue(
            "map_snapshot_id",
            snapshotId);
        invalidQuality.Parameters.AddWithValue(
            "war_region_id",
            fixture.FirstWarRegionId);
        invalidQuality.Parameters.AddWithValue(
            "validation_fetch_id",
            fixture.RepresentationFetchId);
        invalidQuality.Parameters.AddWithValue(
            "started_at",
            fixture.RepresentationRetrievedAt);
        invalidQuality.Parameters.AddWithValue(
            "completed_at",
            fixture.RepresentationRetrievedAt);

        var qualityException =
            await Assert.ThrowsAsync<PostgresException>(
                () => invalidQuality.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken));

        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            qualityException.SqlState);
        Assert.Equal(
            "ck_map_quality_runs_decision",
            qualityException.ConstraintName);
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
            "m6-foundation",
            "M6 Foundation",
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

        var ingestion = new IngestionKernel(
            new PostgresIngestionKernelStore(dataSource));
        var evidence = new EvidenceKernel(
            new PostgresEvidenceKernelStore(dataSource));
        var parseRuns =
            new PostgresSourceParseRunStore(dataSource);

        var representationRetrievedAt =
            new DateTimeOffset(
                2026,
                9,
                28,
                0,
                0,
                0,
                TimeSpan.Zero);

        var representation = await CaptureAsync(
            ingestion,
            evidence,
            endpoint.Resource.Id,
            "m6-foundation-representation",
            representationRetrievedAt,
            200,
            "{}"u8.ToArray(),
            priorFetchId: null);

        var parseStartedAt =
            representationRetrievedAt.AddMilliseconds(1);
        var parse = await parseRuns.RecordAsync(
            new SourceParseRunWrite(
                representation.Fetch!.Id,
                "dynamic-map-state",
                "warapi-adapter@1",
                "warapi-parser@1",
                "json-shape@1",
                "shape-m6-foundation",
                "parsed",
                0,
                0,
                null,
                parseStartedAt,
                parseStartedAt.AddMilliseconds(1),
                sourceVersion: 1,
                sourceLastUpdated: null,
                decodedByteLength: 2),
            TestContext.Current.CancellationToken);

        var normalization = new NormalizationKernel(
            new PostgresNormalizationRunStore(dataSource));
        var normalizationRun = await normalization.RecordAsync(
            new NormalizationRunWrite(
                parse.Id,
                "warapi-dynamic-map-normalizer@1",
                NormalizationRunOutcome.Normalized,
                null,
                representationRetrievedAt.AddMilliseconds(2),
                representationRetrievedAt.AddMilliseconds(3)),
            TestContext.Current.CancellationToken);

        var firstWarId = Guid.CreateVersion7();
        var secondWarId = Guid.CreateVersion7();
        var regionId = Guid.CreateVersion7();
        var firstWarRegionId = Guid.CreateVersion7();
        var secondWarRegionId = Guid.CreateVersion7();

        await using (var context = dataSource.CreateCommand())
        {
            context.CommandText =
                """
                INSERT INTO runtime.wars
                    (id, shard_id, source_war_id, war_number,
                     first_observed_at, last_observed_at)
                VALUES
                    (@war1, @shard_id, 'm6-war-1', 1, @t0, @t0),
                    (@war2, @shard_id, 'm6-war-2', 2, @t1, @t1);

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
                    (@war_region1, @war1, @region_id,
                     'DeadLandsHex', @t0, @t0),
                    (@war_region2, @war2, @region_id,
                     'DeadLandsHex', @t1, @t1);
                """;
            context.Parameters.AddWithValue(
                "war1",
                firstWarId);
            context.Parameters.AddWithValue(
                "war2",
                secondWarId);
            context.Parameters.AddWithValue(
                "shard_id",
                shard.Resource.Id.Value);
            context.Parameters.AddWithValue(
                "region_id",
                regionId);
            context.Parameters.AddWithValue(
                "war_region1",
                firstWarRegionId);
            context.Parameters.AddWithValue(
                "war_region2",
                secondWarRegionId);
            context.Parameters.AddWithValue(
                "t0",
                representationRetrievedAt);
            context.Parameters.AddWithValue(
                "t1",
                representationRetrievedAt.AddMinutes(10));

            await context.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
        }

        return new Fixture(
            dataSource,
            ingestion,
            evidence,
            endpoint.Resource.Id,
            parse.Id.Value,
            normalizationRun.Id.Value,
            representation.Fetch.Id.Value,
            representationRetrievedAt,
            firstWarRegionId,
            secondWarRegionId);
    }

    private static async Task<CaptureResult> CaptureAsync(
        IngestionKernel ingestion,
        EvidenceKernel evidence,
        FoxData.Core.Sources.EndpointId endpointId,
        string idempotencyKey,
        DateTimeOffset retrievedAt,
        int statusCode,
        byte[]? body,
        FetchId? priorFetchId)
    {
        var scheduledAt =
            DateTimeOffset.UtcNow.AddSeconds(-1);
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
        Assert.Equal(endpointId, claim.Job!.EndpointId);

        var attemptId = IngestionAttemptId.New();
        var begun = await ingestion.BeginAttemptAsync(
            attemptId,
            claim.Job.Id,
            workerId,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);
        Assert.Equal(BeginAttemptStatus.Started, begun.Status);

        var fenced = await ingestion.AcquireEndpointFenceAsync(
            attemptId,
            workerId,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            FenceAcquireStatus.AcquiredNow,
            fenced.Status);

        var authorized = await ingestion.AuthorizeExchangeAsync(
            attemptId,
            workerId,
            claim.Job.LeaseGeneration,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            ExchangeAuthorizationStatus.AuthorizedNow,
            authorized.Status);

        var capture = await evidence.CaptureSourceResponseAsync(
            attemptId,
            endpointId,
            claim.Job.LeaseGeneration,
            fenced.Attempt.FenceToken!.Value,
            new SourceResponseObservation(
                retrievedAt.AddMilliseconds(-2),
                retrievedAt.AddMilliseconds(-1),
                retrievedAt,
                "war-api",
                statusCode,
                "application/json",
                null,
                body?.LongLength,
                ""m6-foundation"",
                "max-age=60",
                retrievedAt.AddMinutes(1),
                2),
            body is null
                ? null
                : new ReadOnlyMemory<byte>(body),
            priorFetchId,
            TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStatus.CapturedCurrent, capture.Status);
        return capture;
    }

    private async Task MigrateAsync()
    {
        var options = new DbContextOptionsBuilder<FoxDataDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var context =
            new FoxDataDbContext(options);
        await context.Database.MigrateAsync(
            TestContext.Current.CancellationToken);
    }

    private async Task ResetAsync()
    {
        await using var dataSource =
            NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var command =
            dataSource.CreateCommand(
                """
                TRUNCATE TABLE sources.sources
                RESTART IDENTITY CASCADE;
                """);

        await command.ExecuteNonQueryAsync(
            TestContext.Current.CancellationToken);
    }

    private async Task<bool> TableExistsAsync(
        string schema,
        string table)
    {
        await using var dataSource =
            NpgsqlDataSource.Create(postgres.ConnectionString);
        await using var command = dataSource.CreateCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = @schema
                  AND table_name = @table
                  AND table_type = 'BASE TABLE');
            """);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);

        return (bool)(await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;
    }

    private sealed class Fixture(
        NpgsqlDataSource dataSource,
        IngestionKernel ingestion,
        EvidenceKernel evidence,
        FoxData.Core.Sources.EndpointId endpointId,
        Guid sourceParseRunId,
        Guid normalizationRunId,
        Guid representationFetchId,
        DateTimeOffset representationRetrievedAt,
        Guid firstWarRegionId,
        Guid secondWarRegionId)
        : IAsyncDisposable
    {
        public NpgsqlDataSource DataSource { get; } =
            dataSource;
        public IngestionKernel Ingestion { get; } =
            ingestion;
        public EvidenceKernel Evidence { get; } =
            evidence;
        public FoxData.Core.Sources.EndpointId EndpointId { get; } =
            endpointId;
        public Guid SourceParseRunId { get; } =
            sourceParseRunId;
        public Guid NormalizationRunId { get; } =
            normalizationRunId;
        public Guid RepresentationFetchId { get; } =
            representationFetchId;
        public DateTimeOffset RepresentationRetrievedAt { get; } =
            representationRetrievedAt;
        public Guid FirstWarRegionId { get; } =
            firstWarRegionId;
        public Guid SecondWarRegionId { get; } =
            secondWarRegionId;

        public async Task InsertSnapshotAsync(Guid snapshotId)
        {
            await using var command =
                DataSource.CreateCommand(
                    """
                    INSERT INTO evidence.map_snapshots
                        (id, normalization_run_id,
                         source_parse_run_id,
                         representation_fetch_id,
                         capability_kind, source_map_name,
                         source_region_id,
                         source_scorched_victory_towns,
                         source_version,
                         source_last_updated_ms,
                         source_updated_at,
                         item_count, text_item_count)
                    VALUES
                        (@id, @normalization_run_id,
                         @source_parse_run_id,
                         @representation_fetch_id,
                         'dynamic', 'DeadLandsHex',
                         1, 0, 1, NULL, NULL, 2, 0);
                    """);
            command.Parameters.AddWithValue("id", snapshotId);
            command.Parameters.AddWithValue(
                "normalization_run_id",
                NormalizationRunId);
            command.Parameters.AddWithValue(
                "source_parse_run_id",
                SourceParseRunId);
            command.Parameters.AddWithValue(
                "representation_fetch_id",
                RepresentationFetchId);

            await command.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
        }

        public async Task InsertItemOccurrenceAsync(
            Guid snapshotId,
            Guid occurrenceId,
            int sourceOrdinal,
            string? rawTeamId,
            int? rawIconType,
            double? x,
            double? y,
            int? rawFlags,
            int? rawViewDirection)
        {
            await using var command =
                DataSource.CreateCommand(
                    """
                    INSERT INTO evidence.map_item_occurrences
                        (id, map_snapshot_id, source_ordinal,
                         raw_team_id, raw_icon_type,
                         x, y, raw_flags, raw_view_direction)
                    VALUES
                        (@id, @map_snapshot_id, @source_ordinal,
                         @raw_team_id, @raw_icon_type,
                         @x, @y, @raw_flags, @raw_view_direction);
                    """);
            command.Parameters.AddWithValue(
                "id",
                occurrenceId);
            command.Parameters.AddWithValue(
                "map_snapshot_id",
                snapshotId);
            command.Parameters.AddWithValue(
                "source_ordinal",
                sourceOrdinal);
            command.Parameters.AddWithValue(
                "raw_team_id",
                (object?)rawTeamId ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "raw_icon_type",
                (object?)rawIconType ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "x",
                (object?)x ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "y",
                (object?)y ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "raw_flags",
                (object?)rawFlags ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "raw_view_direction",
                (object?)rawViewDirection ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
        }

        public async Task<FetchId> CreateValidation304Async(
            Guid priorFetchId,
            DateTimeOffset retrievedAt)
        {
            var capture = await CaptureAsync(
                Ingestion,
                Evidence,
                EndpointId,
                "m6-foundation-validation-304",
                retrievedAt,
                304,
                body: null,
                new FetchId(priorFetchId));

            Assert.Null(capture.Fetch!.PayloadId);
            Assert.Equal(
                new FetchId(priorFetchId),
                capture.Fetch.PriorFetchId);

            return capture.Fetch.Id;
        }

        public async Task InsertAcceptedBindingAsync(
            Guid qualityRunId,
            Guid observationId,
            Guid snapshotId,
            Guid warRegionId,
            Guid validationFetchId,
            DateTimeOffset observedAt)
        {
            await using var command =
                DataSource.CreateCommand(
                    """
                    INSERT INTO quality.map_quality_runs
                        (id, map_snapshot_id, war_region_id,
                         validation_fetch_id, taxonomy_version,
                         quality_policy_version, decision,
                         started_at, completed_at)
                    VALUES
                        (@quality_run_id, @map_snapshot_id,
                         @war_region_id, @validation_fetch_id,
                         'warapi-map-taxonomy@1',
                         'warapi-map-quality@1',
                         'accepted', @observed_at, @observed_at);

                    INSERT INTO runtime.map_observations
                        (id, war_region_id, map_snapshot_id,
                         quality_run_id, validation_fetch_id,
                         capability_kind, observed_at,
                         source_updated_at)
                    VALUES
                        (@observation_id, @war_region_id,
                         @map_snapshot_id, @quality_run_id,
                         @validation_fetch_id, 'dynamic',
                         @observed_at, NULL);
                    """);
            command.Parameters.AddWithValue(
                "quality_run_id",
                qualityRunId);
            command.Parameters.AddWithValue(
                "observation_id",
                observationId);
            command.Parameters.AddWithValue(
                "map_snapshot_id",
                snapshotId);
            command.Parameters.AddWithValue(
                "war_region_id",
                warRegionId);
            command.Parameters.AddWithValue(
                "validation_fetch_id",
                validationFetchId);
            command.Parameters.AddWithValue(
                "observed_at",
                observedAt);

            await command.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
        }

        public async Task<long> CountAsync(string table)
        {
            await using var command =
                DataSource.CreateCommand(
                    $"SELECT COUNT(*) FROM {table};");

            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public ValueTask DisposeAsync() =>
            DataSource.DisposeAsync();
    }
}
