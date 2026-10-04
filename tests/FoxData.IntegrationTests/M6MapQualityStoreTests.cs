using FoxData.Application.Canonical;
using FoxData.Application.Evidence;
using FoxData.Application.Ingestion;
using FoxData.Application.Sources;
using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Quality;
using FoxData.Core.Runtime;
using FoxData.Infrastructure.Canonical;
using FoxData.Infrastructure.Evidence;
using FoxData.Infrastructure.Ingestion;
using FoxData.Infrastructure.Persistence;
using FoxData.Infrastructure.Sources;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class M6MapQualityStoreTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task AcceptedQualityAtomicallyCreatesObservationAndEnrichesWarRegion()
    {
        await using var fixture = await CreateFixtureAsync(
            snapshotSourceRegionId: 7,
            warRegionSourceRegionId: null);

        var finding = new MapQualityFindingCandidate(
            "taxonomy.unknown-icon",
            "taxonomy.unknown-icon@1",
            "taxonomy.unknown-icon-config@1",
            MapQualityFindingEffect.Informational,
            fixture.Item.Id,
            null,
            "unknown_icon",
            """{"unknownIconCount":1}""");

        var result = await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(
                MapQualityDecision.Accepted,
                findings: [finding]),
            TestContext.Current.CancellationToken);

        Assert.Equal(MapQualityDecision.Accepted, result.Run.Decision);
        Assert.NotNull(result.Observation);
        Assert.Equal(
            fixture.RepresentationRetrievedAt,
            result.Observation.ObservedAt);
        Assert.Equal(
            fixture.SourceUpdatedAt,
            result.Observation.SourceUpdatedAt);
        Assert.Equal(MapSnapshotKind.Dynamic, result.Observation.Kind);
        Assert.Equal(7, result.WarRegion.SourceRegionId);
        Assert.Single(result.Findings);
        Assert.Equal(
            fixture.Snapshot.Id,
            result.Findings[0].MapSnapshotId);
        Assert.Equal(
            """{"unknownIconCount": 1}""",
            result.Findings[0].InputMetricsJson);

        Assert.Equal(1L, await fixture.CountAsync(
            "quality.map_quality_runs"));
        Assert.Equal(1L, await fixture.CountAsync(
            "quality.map_quality_findings"));
        Assert.Equal(1L, await fixture.CountAsync(
            "runtime.map_observations"));
        Assert.Equal(
            7,
            await fixture.ReadWarRegionSourceRegionIdAsync());
    }

    [Fact]
    public async Task SnapshotLookupByIdReturnsCompleteDurableGraph()
    {
        await using var fixture = await CreateFixtureAsync(
            snapshotSourceRegionId: 7,
            warRegionSourceRegionId: null);

        var loaded = await new MapSnapshotKernel(
                new PostgresMapSnapshotStore(fixture.DataSource))
            .GetByIdAsync(
                fixture.Snapshot.Id,
                TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(fixture.Snapshot, loaded.Snapshot);
        Assert.Single(loaded.Items);
        Assert.Equal(fixture.Item, loaded.Items[0]);
        Assert.Empty(loaded.TextItems);
        Assert.Equal(
            fixture.Snapshot.NormalizationRunId,
            loaded.NormalizationRun.Id);
    }

    [Theory]
    [InlineData(MapQualityDecision.Suspect)]
    [InlineData(MapQualityDecision.Quarantined)]
    public async Task NonAcceptedQualityCreatesNoRuntimeObservationOrEnrichment(
        MapQualityDecision decision)
    {
        await using var fixture = await CreateFixtureAsync(
            snapshotSourceRegionId: 9,
            warRegionSourceRegionId: null);

        var result = await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(
                decision,
                findings:
                [
                    new MapQualityFindingCandidate(
                        "source-time.representable",
                        "source-time.representable@1",
                        "source-time.representable-config@1",
                        decision == MapQualityDecision.Suspect
                            ? MapQualityFindingEffect.Suspect
                            : MapQualityFindingEffect.Quarantined,
                        null,
                        null,
                        "quality_gate",
                        """{"value":1}"""),
                ]),
            TestContext.Current.CancellationToken);

        Assert.Equal(decision, result.Run.Decision);
        Assert.Null(result.Observation);
        Assert.Null(result.WarRegion.SourceRegionId);
        Assert.Equal(0L, await fixture.CountAsync(
            "runtime.map_observations"));
        Assert.Null(
            await fixture.ReadWarRegionSourceRegionIdAsync());
    }

    [Fact]
    public async Task RepeatedQualityWriteReturnsSameDurableGraph()
    {
        await using var fixture = await CreateFixtureAsync(
            snapshotSourceRegionId: 11,
            warRegionSourceRegionId: null);

        var write = fixture.CreateWrite(
            MapQualityDecision.Accepted,
            findings:
            [
                new MapQualityFindingCandidate(
                    "schema.structure-changed",
                    "schema.structure-changed@1",
                    "schema.structure-changed-config@1",
                    MapQualityFindingEffect.Informational,
                    null,
                    null,
                    null,
                    """{"changed":false}"""),
            ]);

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

        Assert.Equal(first.Run.Id, replay.Run.Id);
        Assert.Equal(
            first.Observation!.Id,
            replay.Observation!.Id);
        Assert.Equal(
            first.Findings.Single().Id,
            replay.Findings.Single().Id);
        Assert.Equal(1L, await fixture.CountAsync(
            "quality.map_quality_runs"));
        Assert.Equal(1L, await fixture.CountAsync(
            "quality.map_quality_findings"));
        Assert.Equal(1L, await fixture.CountAsync(
            "runtime.map_observations"));
    }

    [Fact]
    public async Task SourceRegionConflictRollsBackAcceptedQualityTransaction()
    {
        await using var fixture = await CreateFixtureAsync(
            snapshotSourceRegionId: 2,
            warRegionSourceRegionId: 1);

        await Assert.ThrowsAsync<CanonicalStateIntegrityException>(
            () => fixture.Kernel.RecordAsync(
                fixture.CreateWrite(
                    MapQualityDecision.Accepted,
                    findings: []),
                TestContext.Current.CancellationToken));

        Assert.Equal(0L, await fixture.CountAsync(
            "quality.map_quality_runs"));
        Assert.Equal(0L, await fixture.CountAsync(
            "quality.map_quality_findings"));
        Assert.Equal(0L, await fixture.CountAsync(
            "runtime.map_observations"));
        Assert.Equal(
            1,
            await fixture.ReadWarRegionSourceRegionIdAsync());
    }

    [Fact]
    public async Task FindingCannotReferenceOccurrenceFromAnotherSnapshot()
    {
        await using var fixture = await CreateFixtureAsync(
            snapshotSourceRegionId: null,
            warRegionSourceRegionId: null);

        var other = await fixture.CreateAuxiliarySnapshotAsync();

        await Assert.ThrowsAsync<CanonicalStateIntegrityException>(
            () => fixture.Kernel.RecordAsync(
                fixture.CreateWrite(
                    MapQualityDecision.Suspect,
                    findings:
                    [
                        new MapQualityFindingCandidate(
                            "taxonomy.unknown-team",
                            "taxonomy.unknown-team@1",
                            "taxonomy.unknown-team-config@1",
                            MapQualityFindingEffect.Informational,
                            other.Items.Single().Id,
                            null,
                            "wrong_snapshot",
                            """{"unknownTeamCount":1}"""),
                    ]),
                TestContext.Current.CancellationToken));

        Assert.Equal(0L, await fixture.CountAsync(
            "quality.map_quality_runs"));
    }

    [Fact]
    public async Task AcceptedBaselineAnd304ReuseSameSnapshotWithoutOccurrenceDuplication()
    {
        await using var fixture = await CreateFixtureAsync(
            snapshotSourceRegionId: 13,
            warRegionSourceRegionId: null);

        var first = await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(
                MapQualityDecision.Accepted,
                findings: []),
            TestContext.Current.CancellationToken);

        var validationFetch = await fixture.CreateValidation304Async(
            fixture.RepresentationRetrievedAt.AddMinutes(10));

        var second = await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(
                MapQualityDecision.Accepted,
                validationFetchId: validationFetch,
                baselineMapObservationId: first.Observation!.Id,
                findings: []),
            TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Run.Id, second.Run.Id);
        Assert.Equal(
            fixture.Snapshot.Id,
            second.Observation!.MapSnapshotId);
        Assert.Equal(
            validationFetch,
            second.Observation.ValidationFetchId);
        Assert.Equal(
            fixture.RepresentationRetrievedAt.AddMinutes(10),
            second.Observation.ObservedAt);
        Assert.Equal(
            1L,
            await fixture.CountAsync("evidence.map_snapshots"));
        Assert.Equal(
            1L,
            await fixture.CountAsync(
                "evidence.map_item_occurrences"));
        Assert.Equal(
            2L,
            await fixture.CountAsync(
                "runtime.map_observations"));
    }

    [Fact]
    public async Task LaterValidationWaitsForEarlier200And304Quality()
    {
        await using var fixture = await CreateFixtureAsync(
            snapshotSourceRegionId: 13,
            warRegionSourceRegionId: null);
        var validation1 = await fixture.CreateValidation304Async(
            fixture.RepresentationRetrievedAt.AddMinutes(10),
            "m6-g6-validation-304-1");
        var validation2 = await fixture.CreateValidation304Async(
            fixture.RepresentationRetrievedAt.AddMinutes(20),
            "m6-g6-validation-304-2");
        var ordering = new PostgresMapQualityOrderingReader(
            fixture.DataSource);

        var first304Deferred = await ordering.GetPlanAsync(
            fixture.Snapshot.Id,
            fixture.WarRegionId,
            validation1,
            "warapi-map-taxonomy@1",
            "warapi-map-quality@1",
            TestContext.Current.CancellationToken);
        Assert.Equal(
            MapQualityOrderingStatus.Deferred,
            first304Deferred.Status);
        Assert.Equal(
            "earlier_quality_missing",
            first304Deferred.DeferredReason);

        var first = await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(MapQualityDecision.Accepted),
            TestContext.Current.CancellationToken);

        var second304Deferred = await ordering.GetPlanAsync(
            fixture.Snapshot.Id,
            fixture.WarRegionId,
            validation2,
            "warapi-map-taxonomy@1",
            "warapi-map-quality@1",
            TestContext.Current.CancellationToken);
        Assert.Equal(
            MapQualityOrderingStatus.Deferred,
            second304Deferred.Status);
        Assert.Equal(
            "earlier_quality_missing",
            second304Deferred.DeferredReason);

        var ready1 = await ordering.GetPlanAsync(
            fixture.Snapshot.Id,
            fixture.WarRegionId,
            validation1,
            "warapi-map-taxonomy@1",
            "warapi-map-quality@1",
            TestContext.Current.CancellationToken);
        Assert.Equal(MapQualityOrderingStatus.Ready, ready1.Status);
        Assert.Equal(first.Observation!.Id, ready1.Baseline!.Id);

        var second = await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(
                MapQualityDecision.Accepted,
                validationFetchId: validation1,
                baselineMapObservationId: ready1.Baseline.Id),
            TestContext.Current.CancellationToken);

        var ready2 = await ordering.GetPlanAsync(
            fixture.Snapshot.Id,
            fixture.WarRegionId,
            validation2,
            "warapi-map-taxonomy@1",
            "warapi-map-quality@1",
            TestContext.Current.CancellationToken);
        Assert.Equal(MapQualityOrderingStatus.Ready, ready2.Status);
        Assert.Equal(second.Observation!.Id, ready2.Baseline!.Id);

        var third = await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(
                MapQualityDecision.Accepted,
                validationFetchId: validation2,
                baselineMapObservationId: ready2.Baseline.Id),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            second.Observation.Id,
            third.Run.BaselineMapObservationId);
        Assert.Equal(
            1L,
            await fixture.CountAsync("evidence.map_snapshots"));
        Assert.Equal(
            1L,
            await fixture.CountAsync("evidence.map_item_occurrences"));
    }

    [Fact]
    public async Task OmittingLatestAcceptedBaselineFailsClosed()
    {
        await using var fixture = await CreateFixtureAsync(
            snapshotSourceRegionId: null,
            warRegionSourceRegionId: null);
        var first = await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(MapQualityDecision.Accepted),
            TestContext.Current.CancellationToken);
        var validation = await fixture.CreateValidation304Async(
            fixture.RepresentationRetrievedAt.AddMinutes(10));

        var exception =
            await Assert.ThrowsAsync<MapQualityOrderingDeferredException>(
                () => fixture.Kernel.RecordAsync(
                    fixture.CreateWrite(
                        MapQualityDecision.Accepted,
                        validationFetchId: validation),
                    TestContext.Current.CancellationToken));
        Assert.Equal("quality_baseline_changed", exception.Reason);
        Assert.Equal(1L, await fixture.CountAsync(
            "quality.map_quality_runs"));
        Assert.Equal(1L, await fixture.CountAsync(
            "runtime.map_observations"));

        var next = await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(
                MapQualityDecision.Accepted,
                validationFetchId: validation,
                baselineMapObservationId: first.Observation!.Id),
            TestContext.Current.CancellationToken);
        Assert.Equal(first.Observation.Id, next.Run.BaselineMapObservationId);
    }

    [Fact]
    public async Task QualityGapReaderFindsVersioned200And304Bindings()
    {
        await using var fixture = await CreateFixtureAsync(
            snapshotSourceRegionId: null,
            warRegionSourceRegionId: null);
        _ = await fixture.CreateAuxiliarySnapshotAsync();
        var snapshotCountBefore304 =
            await fixture.CountAsync("evidence.map_snapshots");
        var occurrenceCountBefore304 =
            await fixture.CountAsync("evidence.map_item_occurrences");

        var gaps = new PostgresMapQualityGapReader(
            fixture.DataSource);
        var plan = new CoverageCapabilityPlan[]
        {
            new(
                "dynamic-map-state",
                "warapi-parser@1",
                "warapi-dynamic-map-normalizer@1",
                DependencyRank: 0),
        };

        var before = await gaps.GetPendingAsync(
            "official-war-api",
            plan,
            "warapi-map-taxonomy@1",
            "warapi-map-quality@1",
            null,
            null,
            64,
            TestContext.Current.CancellationToken);

        var body = Assert.Single(before);
        Assert.Equal(
            MapQualityGapValidationKind.BodyBearing200,
            body.ValidationKind);
        Assert.Equal(
            fixture.RepresentationFetchId,
            body.ValidationFetchId);
        Assert.Equal(
            fixture.RepresentationRetrievedAt,
            body.RepresentationObservedAt);
        Assert.Equal(
            fixture.RepresentationRetrievedAt,
            body.ObservedAt);
        Assert.Equal(
            "dynamic-map-state",
            body.CapabilityKey);
        Assert.Equal(
            fixture.Snapshot.NormalizationRunId,
            body.NormalizationRunId);

        await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(MapQualityDecision.Suspect),
            TestContext.Current.CancellationToken);

        var sameTimestampValidation =
            await fixture.CreateValidation304Async(
                fixture.RepresentationRetrievedAt,
                "m6-g6-same-timestamp-304");

        var sameTimestampPending = await gaps.GetPendingAsync(
            "official-war-api",
            plan,
            "warapi-map-taxonomy@1",
            "warapi-map-quality@1",
            null,
            null,
            64,
            TestContext.Current.CancellationToken);

        var sameTimestamp = Assert.Single(sameTimestampPending);
        Assert.Equal(
            MapQualityGapValidationKind.NotModified304,
            sameTimestamp.ValidationKind);
        Assert.Equal(
            sameTimestampValidation,
            sameTimestamp.ValidationFetchId);
        Assert.Equal(
            fixture.RepresentationRetrievedAt,
            sameTimestamp.ObservedAt);

        await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(
                MapQualityDecision.Suspect,
                validationFetchId: sameTimestampValidation),
            TestContext.Current.CancellationToken);

        var validationAt =
            fixture.RepresentationRetrievedAt.AddMinutes(10);
        var validation = await fixture.CreateValidation304Async(
            validationAt,
            "m6-g6-gap-reader-304");

        var pending304 = await gaps.GetPendingAsync(
            "official-war-api",
            plan,
            "warapi-map-taxonomy@1",
            "warapi-map-quality@1",
            null,
            null,
            64,
            TestContext.Current.CancellationToken);

        var notModified = Assert.Single(pending304);
        Assert.Equal(
            MapQualityGapValidationKind.NotModified304,
            notModified.ValidationKind);
        Assert.Equal(validation, notModified.ValidationFetchId);
        Assert.Equal(
            fixture.RepresentationRetrievedAt,
            notModified.RepresentationObservedAt);
        Assert.Equal(validationAt, notModified.ObservedAt);
        Assert.Equal(
            fixture.Snapshot.NormalizationRunId,
            notModified.NormalizationRunId);

        await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(
                MapQualityDecision.Suspect,
                validationFetchId: validation),
            TestContext.Current.CancellationToken);

        var after = await gaps.GetPendingAsync(
            "official-war-api",
            plan,
            "warapi-map-taxonomy@1",
            "warapi-map-quality@1",
            null,
            null,
            64,
            TestContext.Current.CancellationToken);
        Assert.Empty(after);
        Assert.Equal(
            snapshotCountBefore304,
            await fixture.CountAsync("evidence.map_snapshots"));
        Assert.Equal(
            occurrenceCountBefore304,
            await fixture.CountAsync("evidence.map_item_occurrences"));

        var reversedValidation =
            await fixture.CreateValidation304Async(
                fixture.RepresentationRetrievedAt.AddMinutes(-1),
                "m6-g6-reversed-304");

        var afterReversed = await gaps.GetPendingAsync(
            "official-war-api",
            plan,
            "warapi-map-taxonomy@1",
            "warapi-map-quality@1",
            null,
            null,
            64,
            TestContext.Current.CancellationToken);
        Assert.Empty(afterReversed);

        var ordering = new PostgresMapQualityOrderingReader(
            fixture.DataSource);
        await Assert.ThrowsAsync<CanonicalStateIntegrityException>(
            () => ordering.GetPlanAsync(
                fixture.Snapshot.Id,
                fixture.WarRegionId,
                reversedValidation,
                "warapi-map-taxonomy@1",
                "warapi-map-quality@1",
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(
            () => gaps.GetPendingAsync(
                "official-war-api",
                [plan[0], plan[0]],
                "warapi-map-taxonomy@1",
                "warapi-map-quality@1",
                null,
                null,
                64,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentSameIdentityQualityWritesConverge()
    {
        await using var fixture = await CreateFixtureAsync(
            snapshotSourceRegionId: 13,
            warRegionSourceRegionId: null);
        var write = fixture.CreateWrite(MapQualityDecision.Accepted);

        var both = await Task.WhenAll(
            fixture.Kernel.RecordAsync(
                write,
                TestContext.Current.CancellationToken),
            fixture.Kernel.RecordAsync(
                write,
                TestContext.Current.CancellationToken));

        Assert.Equal(both[0].Run.Id, both[1].Run.Id);
        Assert.Equal(both[0].Observation!.Id, both[1].Observation!.Id);
        Assert.Equal(1L, await fixture.CountAsync(
            "quality.map_quality_runs"));
        Assert.Equal(1L, await fixture.CountAsync(
            "runtime.map_observations"));
    }

    [Fact]
    public async Task QualityStoreRejectsCrossShardWarRegion()
    {
        await using var fixture = await CreateFixtureAsync(
            snapshotSourceRegionId: null,
            warRegionSourceRegionId: null);
        await using (var command = fixture.DataSource.CreateCommand())
        {
            command.CommandText =
                """
                WITH new_shard AS
                (
                    INSERT INTO sources.shards
                        (id, source_id, key, display_name, environment)
                    SELECT
                        @new_shard_id,
                        shard.source_id,
                        'quality-test-other-shard',
                        'Other test shard',
                        'live'
                    FROM sources.shards AS shard
                    JOIN sources.endpoints AS endpoint
                        ON endpoint.shard_id = shard.id
                    WHERE endpoint.id = @endpoint_id
                    RETURNING id
                )
                UPDATE runtime.wars
                SET shard_id = (SELECT id FROM new_shard)
                WHERE id = (
                    SELECT war_id
                    FROM runtime.war_regions
                    WHERE id = @war_region_id
                );
                """;
            command.Parameters.AddWithValue(
                "new_shard_id",
                Guid.CreateVersion7());
            command.Parameters.AddWithValue(
                "endpoint_id",
                fixture.EndpointId.Value);
            command.Parameters.AddWithValue(
                "war_region_id",
                fixture.WarRegionId.Value);
            await command.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<CanonicalStateIntegrityException>(
            () => fixture.Kernel.RecordAsync(
                fixture.CreateWrite(MapQualityDecision.Accepted),
                TestContext.Current.CancellationToken));
        Assert.Equal(0L, await fixture.CountAsync(
            "runtime.map_observations"));
    }

    [Fact]
    public async Task ConcurrentDistinctBodyCandidatesCannotBypassChronology()
    {
        await using var fixture = await CreateFixtureAsync(
            snapshotSourceRegionId: null,
            warRegionSourceRegionId: null);
        var later = await fixture.CreateLater200Async(
            fixture.RepresentationRetrievedAt.AddMinutes(5));
        var laterWrite = fixture.CreateWrite(
            MapQualityDecision.Accepted) with
        {
            MapSnapshotId = later.Snapshot.Snapshot.Id,
            ValidationFetchId = later.FetchId,
            StartedAt = later.RetrievedAt.AddSeconds(1),
            CompletedAt = later.RetrievedAt.AddSeconds(2),
        };

        // Both Workers attempt writes for different durable Fetches.
        // The later candidate must never commit with a missing baseline.
        var laterTask = fixture.Kernel.RecordAsync(
            laterWrite,
            TestContext.Current.CancellationToken);
        var earlierTask = fixture.Kernel.RecordAsync(
            fixture.CreateWrite(MapQualityDecision.Accepted),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<MapQualityOrderingDeferredException>(
            () => laterTask);
        var earlier = await earlierTask;
        Assert.Equal(1L, await fixture.CountAsync(
            "runtime.map_observations"));

        var acceptedLater = await fixture.Kernel.RecordAsync(
            laterWrite with
            {
                BaselineMapObservationId = earlier.Observation!.Id,
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(
            earlier.Observation.Id,
            acceptedLater.Run.BaselineMapObservationId);
        Assert.Equal(2L, await fixture.CountAsync(
            "runtime.map_observations"));
    }

    [Fact]
    public async Task Late304CannotRewriteAlreadyTerminalBaselineChain()
    {
        await using var fixture = await CreateFixtureAsync(
            snapshotSourceRegionId: null,
            warRegionSourceRegionId: null);
        var first = await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(MapQualityDecision.Accepted),
            TestContext.Current.CancellationToken);
        var later = await fixture.CreateValidation304Async(
            fixture.RepresentationRetrievedAt.AddMinutes(20),
            "validation-later");
        await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(
                MapQualityDecision.Accepted,
                validationFetchId: later,
                baselineMapObservationId: first.Observation!.Id),
            TestContext.Current.CancellationToken);

        // Simulates local out-of-order discovery of earlier validation.
        var older = await fixture.CreateValidation304Async(
            fixture.RepresentationRetrievedAt.AddMinutes(10),
            "validation-older");
        var exception =
            await Assert.ThrowsAsync<MapQualityOrderingDeferredException>(
                () => fixture.Kernel.RecordAsync(
                    fixture.CreateWrite(
                        MapQualityDecision.Accepted,
                        validationFetchId: older,
                        baselineMapObservationId: first.Observation.Id),
                    TestContext.Current.CancellationToken));

        Assert.Equal(
            MapQualityDeferredReasons.LaterQualityAlreadyTerminal,
            exception.Reason);
        Assert.Equal(2L, await fixture.CountAsync(
            "runtime.map_observations"));
    }

    [Fact]
    public async Task PolicyV2QuarantineDoesNotReplaceAcceptedBaseline()
    {
        await using var fixture = await CreateFixtureAsync(
            snapshotSourceRegionId: null,
            warRegionSourceRegionId: null);

        var first = await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(
                MapQualityDecision.Accepted,
                policyVersion: "warapi-map-quality@2"),
            TestContext.Current.CancellationToken);
        Assert.NotNull(first.Observation);

        var quarantinedValidation =
            await fixture.CreateValidation304Async(
                fixture.RepresentationRetrievedAt.AddMinutes(10),
                "m6-f2-quarantined");
        var quarantined = await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(
                MapQualityDecision.Quarantined,
                validationFetchId: quarantinedValidation,
                baselineMapObservationId: first.Observation!.Id,
                policyVersion: "warapi-map-quality@2"),
            TestContext.Current.CancellationToken);
        Assert.Null(quarantined.Observation);

        var recoveryValidation =
            await fixture.CreateValidation304Async(
                fixture.RepresentationRetrievedAt.AddMinutes(20),
                "m6-f2-recovery");
        var recovered = await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(
                MapQualityDecision.Accepted,
                validationFetchId: recoveryValidation,
                baselineMapObservationId: first.Observation.Id,
                policyVersion: "warapi-map-quality@2"),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            first.Observation.Id,
            recovered.Run.BaselineMapObservationId);
        Assert.Equal(
            2L,
            await fixture.CountAsync(
                "runtime.map_observations"));
    }

    [Fact]
    public async Task DatabaseRejectsObservationForNonAcceptedQualityRun()
    {
        await using var fixture = await CreateFixtureAsync(
            snapshotSourceRegionId: null,
            warRegionSourceRegionId: null);

        var suspect = await fixture.Kernel.RecordAsync(
            fixture.CreateWrite(
                MapQualityDecision.Suspect,
                findings: []),
            TestContext.Current.CancellationToken);

        await using var command = fixture.DataSource.CreateCommand(
            """
            INSERT INTO runtime.map_observations
                (id, war_region_id, map_snapshot_id,
                 quality_run_id, validation_fetch_id,
                 quality_decision, capability_kind,
                 observed_at, source_updated_at)
            VALUES
                (@id, @war_region_id, @map_snapshot_id,
                 @quality_run_id, @validation_fetch_id,
                 'accepted', 'dynamic',
                 @observed_at, NULL);
            """);
        command.Parameters.AddWithValue(
            "id",
            Guid.CreateVersion7());
        command.Parameters.AddWithValue(
            "war_region_id",
            fixture.WarRegionId.Value);
        command.Parameters.AddWithValue(
            "map_snapshot_id",
            fixture.Snapshot.Id.Value);
        command.Parameters.AddWithValue(
            "quality_run_id",
            suspect.Run.Id.Value);
        command.Parameters.AddWithValue(
            "validation_fetch_id",
            fixture.RepresentationFetchId.Value);
        command.Parameters.AddWithValue(
            "observed_at",
            fixture.RepresentationRetrievedAt);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(
            PostgresErrorCodes.ForeignKeyViolation,
            exception.SqlState);
        Assert.Equal(
            "FK_map_observations_quality_run_decision",
            exception.ConstraintName);
    }

    private async Task<Fixture> CreateFixtureAsync(
        int? snapshotSourceRegionId,
        int? warRegionSourceRegionId)
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

        var ingestion = new IngestionKernel(
            new PostgresIngestionKernelStore(dataSource));
        var evidence = new EvidenceKernel(
            new PostgresEvidenceKernelStore(dataSource));
        var parseRuns =
            new PostgresSourceParseRunStore(dataSource);
        var snapshotKernel = new MapSnapshotKernel(
            new PostgresMapSnapshotStore(dataSource));

        var retrievedAt = new DateTimeOffset(
            2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
        var sourceUpdatedAt = retrievedAt.AddMinutes(-1);

        var representation = await CaptureAsync(
            ingestion,
            evidence,
            endpoint.Resource.Id,
            "m6-e2-representation",
            retrievedAt,
            200,
            "{}"u8.ToArray(),
            priorFetchId: null);

        var parseStartedAt = retrievedAt.AddMilliseconds(1);
        var parse = await parseRuns.RecordAsync(
            new SourceParseRunWrite(
                representation.Fetch!.Id,
                "dynamic-map-state",
                "warapi-adapter@1",
                "warapi-parser@1",
                "json-shape@1",
                "shape-m6-e2",
                "parsed",
                0,
                0,
                null,
                parseStartedAt,
                parseStartedAt.AddMilliseconds(1),
                SourceVersion: 1,
                SourceLastUpdated:
                    sourceUpdatedAt.ToUnixTimeMilliseconds(),
                DecodedByteLength: 2),
            TestContext.Current.CancellationToken);

        var snapshot = await snapshotKernel.RecordAcceptedAsync(
            new MapSnapshotWrite(
                parse.Id,
                "warapi-dynamic-map-normalizer@1",
                "dynamic-map-state",
                "map-dynamic/DeadLandsHex",
                retrievedAt.AddMilliseconds(2),
                retrievedAt.AddMilliseconds(3),
                representation.Fetch.Id,
                MapSnapshotKind.Dynamic,
                "DeadLandsHex",
                snapshotSourceRegionId,
                0,
                1,
                sourceUpdatedAt.ToUnixTimeMilliseconds(),
                sourceUpdatedAt,
                SourceMapItemsArrayPresent: true,
                SourceMapTextItemsArrayPresent: true,
                Items:
                [
                    new MapItemOccurrenceCandidate(
                        0,
                        "WARDENS",
                        97,
                        0.5d,
                        0.25d,
                        0,
                        0),
                ],
                TextItems: []),
            TestContext.Current.CancellationToken);

        var warId = WarId.New();
        var regionId = RegionId.New();
        var warRegionId = WarRegionId.New();

        await using (var command = dataSource.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO runtime.wars
                    (id, shard_id, source_war_id, war_number,
                     first_observed_at, last_observed_at)
                VALUES
                    (@war_id, @shard_id, 'm6-e2-war', 1,
                     @observed_at, @observed_at);

                INSERT INTO runtime.regions
                    (id, canonical_key, display_name)
                VALUES
                    (@region_id,
                     'official-war-api/map/DeadLandsHex',
                     'DeadLandsHex');

                INSERT INTO runtime.war_regions
                    (id, war_id, region_id, source_map_name,
                     source_region_id,
                     first_seen_at, last_seen_at)
                VALUES
                    (@war_region_id, @war_id, @region_id,
                     'DeadLandsHex', @source_region_id,
                     @observed_at, @observed_at);
                """;
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
            command.Parameters.AddWithValue(
                "source_region_id",
                (object?)warRegionSourceRegionId ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "observed_at",
                retrievedAt);

            await command.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
        }

        return new Fixture(
            dataSource,
            ingestion,
            evidence,
            parseRuns,
            endpoint.Resource.Id,
            parse.Id,
            snapshotKernel,
            new MapQualityKernel(
                new PostgresMapQualityStore(dataSource)),
            snapshot.Snapshot,
            snapshot.Items.Single(),
            warRegionId,
            representation.Fetch.Id,
            retrievedAt,
            sourceUpdatedAt);
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

        ReadOnlyMemory<byte>? capturedBody = null;
        if (body is not null)
        {
            capturedBody = new ReadOnlyMemory<byte>(body);
        }

        var capture = await evidence.CaptureSourceResponseAsync(
            attemptId,
            endpointId,
            claim.Job.LeaseGeneration,
            fenced.Attempt!.FenceToken!.Value,
            new SourceResponseObservation(
                retrievedAt.AddMilliseconds(-2),
                retrievedAt.AddMilliseconds(-1),
                retrievedAt,
                "war-api",
                statusCode,
                "application/json",
                null,
                body?.LongLength,
                null,
                "max-age=60",
                retrievedAt.AddMinutes(1),
                2),
            capturedBody,
            priorFetchId,
            TestContext.Current.CancellationToken);

        Assert.Equal(CaptureStatus.CapturedCurrent, capture.Status);
        return capture;
    }

    private sealed class Fixture(
        NpgsqlDataSource dataSource,
        IngestionKernel ingestion,
        EvidenceKernel evidence,
        ISourceParseRunStore parseRuns,
        FoxData.Core.Sources.EndpointId endpointId,
        SourceParseRunId sourceParseRunId,
        MapSnapshotKernel snapshotKernel,
        MapQualityKernel kernel,
        MapSnapshotDescriptor snapshot,
        MapItemOccurrenceDescriptor item,
        WarRegionId warRegionId,
        FetchId representationFetchId,
        DateTimeOffset representationRetrievedAt,
        DateTimeOffset sourceUpdatedAt)
        : IAsyncDisposable
    {
        public NpgsqlDataSource DataSource { get; } = dataSource;
        public FoxData.Core.Sources.EndpointId EndpointId { get; } = endpointId;
        public MapQualityKernel Kernel { get; } = kernel;
        public MapSnapshotDescriptor Snapshot { get; } = snapshot;
        public MapItemOccurrenceDescriptor Item { get; } = item;
        public WarRegionId WarRegionId { get; } = warRegionId;
        public FetchId RepresentationFetchId { get; } =
            representationFetchId;
        public DateTimeOffset RepresentationRetrievedAt { get; } =
            representationRetrievedAt;
        public DateTimeOffset SourceUpdatedAt { get; } =
            sourceUpdatedAt;

        public MapQualityWrite CreateWrite(
            MapQualityDecision decision,
            FetchId? validationFetchId = null,
            MapObservationId? baselineMapObservationId = null,
            IReadOnlyList<MapQualityFindingCandidate>? findings = null,
            string policyVersion = "warapi-map-quality@1") =>
            new(
                Snapshot.Id,
                WarRegionId,
                validationFetchId ?? RepresentationFetchId,
                "warapi-map-taxonomy@1",
                policyVersion,
                baselineMapObservationId,
                decision,
                RepresentationRetrievedAt.AddSeconds(1),
                RepresentationRetrievedAt.AddSeconds(2),
                findings ?? []);

        public async Task<FetchId> CreateValidation304Async(
            DateTimeOffset retrievedAt,
            string idempotencyKey = "m6-e2-validation-304")
        {
            var capture = await CaptureAsync(
                ingestion,
                evidence,
                EndpointId,
                idempotencyKey,
                retrievedAt,
                304,
                body: null,
                priorFetchId: RepresentationFetchId);

            return capture.Fetch!.Id;
        }

        public async Task<(
            MapSnapshotResult Snapshot,
            FetchId FetchId,
            DateTimeOffset RetrievedAt)> CreateLater200Async(
            DateTimeOffset retrievedAt)
        {
            var response = await CaptureAsync(
                ingestion,
                evidence,
                EndpointId,
                "later-distinct-body-200",
                retrievedAt,
                200,
                "{}"u8.ToArray(),
                priorFetchId: null);
            var parse = await parseRuns.RecordAsync(
                new SourceParseRunWrite(
                    response.Fetch!.Id,
                    "dynamic-map-state",
                    "warapi-adapter@1",
                    "warapi-parser@1",
                    "json-shape@1",
                    "shape-m6-e2",
                    "parsed",
                    0,
                    0,
                    null,
                    retrievedAt.AddMilliseconds(1),
                    retrievedAt.AddMilliseconds(2),
                    SourceVersion: 2,
                    SourceLastUpdated:
                        SourceUpdatedAt.ToUnixTimeMilliseconds(),
                    DecodedByteLength: 2),
                TestContext.Current.CancellationToken);

            var normalized = await snapshotKernel.RecordAcceptedAsync(
                new MapSnapshotWrite(
                    parse.Id,
                    "warapi-dynamic-map-normalizer@1",
                    "dynamic-map-state",
                    "map-dynamic/DeadLandsHex",
                    retrievedAt.AddMilliseconds(3),
                    retrievedAt.AddMilliseconds(4),
                    response.Fetch.Id,
                    MapSnapshotKind.Dynamic,
                    "DeadLandsHex",
                    Snapshot.SourceRegionId,
                    0,
                    2,
                    SourceUpdatedAt.ToUnixTimeMilliseconds(),
                    SourceUpdatedAt,
                    SourceMapItemsArrayPresent: true,
                    SourceMapTextItemsArrayPresent: true,
                    Items:
                    [
                        new MapItemOccurrenceCandidate(
                            0,
                            "WARDENS",
                            97,
                            0.5d,
                            0.25d,
                            0,
                            0),
                    ],
                    TextItems: []),
                TestContext.Current.CancellationToken);
            return (normalized, response.Fetch.Id, retrievedAt);
        }

        public async Task<MapSnapshotResult> CreateAuxiliarySnapshotAsync()
        {
            return await snapshotKernel.RecordAcceptedAsync(
                new MapSnapshotWrite(
                    sourceParseRunId,
                    "test-aux-map-normalizer@1",
                    "dynamic-map-state",
                    "map-dynamic/DeadLandsHex",
                    RepresentationRetrievedAt.AddSeconds(3),
                    RepresentationRetrievedAt.AddSeconds(4),
                    RepresentationFetchId,
                    MapSnapshotKind.Dynamic,
                    "DeadLandsHex",
                    Snapshot.SourceRegionId,
                    0,
                    1,
                    SourceUpdatedAt.ToUnixTimeMilliseconds(),
                    SourceUpdatedAt,
                    SourceMapItemsArrayPresent: true,
                    SourceMapTextItemsArrayPresent: true,
                    Items:
                    [
                        new MapItemOccurrenceCandidate(
                            0,
                            "COLONIALS",
                            97,
                            0.8d,
                            0.8d,
                            32,
                            90),
                    ],
                    TextItems: []),
                TestContext.Current.CancellationToken);
        }

        public async Task<int?> ReadWarRegionSourceRegionIdAsync()
        {
            await using var command = DataSource.CreateCommand(
                """
                SELECT source_region_id
                FROM runtime.war_regions
                WHERE id = @id;
                """);
            command.Parameters.AddWithValue(
                "id",
                WarRegionId.Value);

            var value = await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken);
            return value is DBNull or null
                ? null
                : (int)value;
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
