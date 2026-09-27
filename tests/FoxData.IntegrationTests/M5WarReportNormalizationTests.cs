using System.Text;
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
using FoxData.Sources.Abstractions;
using FoxData.Sources.WarApi;
using FoxData.Worker;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoxData.IntegrationTests;

public sealed class M5WarReportNormalizationTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ReportNormalizationBuildsMissingWarAndRegionContextFromDurableEvidence()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 14, 0, 0, TimeSpan.Zero);

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-report-war",
            """{"warId":"war-report-129","warNumber":129,"winner":"NONE"}""",
            start);

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "war-report-maps",
            """["DeadLandsHex","MarbanHollow"]""",
            start.AddMinutes(1));

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "war-report-deadlands",
            """
            {
              "totalEnlistments":148,
              "colonialCasualties":202,
              "wardenCasualties":222,
              "dayOfWar":2
            }
            """,
            start.AddMinutes(2));

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Normalized,
            result.Status);
        Assert.NotNull(result.Canonical);

        var canonical = result.Canonical;
        Assert.Equal("DeadLandsHex", canonical.WarRegion.SourceMapName);
        Assert.Equal(start.AddMinutes(1), canonical.WarRegion.FirstSeenAt);
        Assert.Equal(start.AddMinutes(1), canonical.WarRegion.LastSeenAt);
        Assert.Equal(start.AddMinutes(2), canonical.Observation.ObservedAt);
        Assert.Equal(148L, canonical.Observation.TotalEnlistments);
        Assert.Equal(202L, canonical.Observation.ColonialCasualties);
        Assert.Equal(222L, canonical.Observation.WardenCasualties);
        Assert.Equal(2, canonical.Observation.DayOfWar);
        Assert.Equal(
            report.RepresentationFetchId,
            canonical.Observation.RepresentationFetchId);

        Assert.Equal(1L, await fixture.CountAsync("runtime.wars"));
        Assert.Equal(2L, await fixture.CountAsync("runtime.war_regions"));
        Assert.Equal(
            1L,
            await fixture.CountAsync("runtime.war_report_observations"));
        Assert.Equal(
            1L,
            await fixture.CountNormalizationRunsAsync(
                WarApiVersions.WarReportNormalizer));
    }

    [Fact]
    public async Task ReportReplayReturnsSameImmutableObservation()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 15, 0, 0, TimeSpan.Zero);

        await fixture.CreateBaseContextAsync(
            start,
            "war-report-replay",
            "DeadLandsHex");

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "report-replay",
            """{"totalEnlistments":10,"colonialCasualties":20,"wardenCasualties":30,"dayOfWar":4}""",
            start.AddMinutes(2));

        var first = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);
        var replay = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Normalized,
            first.Status);
        Assert.Equal(
            WarApiWarReportNormalizationStatus.Normalized,
            replay.Status);
        Assert.NotNull(first.Canonical);
        Assert.NotNull(replay.Canonical);
        Assert.Equal(
            first.NormalizationRun!.Id,
            replay.NormalizationRun!.Id);
        Assert.Equal(
            first.Canonical.Observation.Id,
            replay.Canonical.Observation.Id);
        Assert.Equal(
            first.Canonical.WarRegion.Id,
            replay.Canonical.WarRegion.Id);
        Assert.Equal(
            1L,
            await fixture.CountAsync("runtime.war_report_observations"));
        Assert.Equal(
            1L,
            await fixture.CountNormalizationRunsAsync(
                WarApiVersions.WarReportNormalizer));
    }

    [Fact]
    public async Task NegativeReportCounterRejectsWithoutCanonicalObservation()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 16, 0, 0, TimeSpan.Zero);

        await fixture.CreateBaseContextAsync(
            start,
            "war-report-negative",
            "DeadLandsHex");

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "report-negative",
            """{"totalEnlistments":10,"colonialCasualties":-1,"wardenCasualties":30,"dayOfWar":4}""",
            start.AddMinutes(2));

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Rejected,
            result.Status);
        Assert.Equal(
            "negative_colonial_casualties",
            result.NormalizationRun!.ErrorCode);
        Assert.Null(result.Canonical);
        Assert.Equal(
            0L,
            await fixture.CountAsync("runtime.war_report_observations"));
    }

    [Fact]
    public async Task UnknownReportPropertiesRemainRepresentable()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 17, 0, 0, TimeSpan.Zero);

        await fixture.CreateBaseContextAsync(
            start,
            "war-report-unknown",
            "DeadLandsHex");

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "report-unknown",
            """{"totalEnlistments":null,"colonialCasualties":20,"wardenCasualties":null,"dayOfWar":5,"futureField":"kept-in-evidence"}""",
            start.AddMinutes(2));

        Assert.Equal("parsed_with_unknowns", report.Outcome);

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Normalized,
            result.Status);
        Assert.NotNull(result.Canonical);
        Assert.Null(result.Canonical.Observation.TotalEnlistments);
        Assert.Equal(20L, result.Canonical.Observation.ColonialCasualties);
        Assert.Null(result.Canonical.Observation.WardenCasualties);
        Assert.Equal(5, result.Canonical.Observation.DayOfWar);
    }

    [Fact]
    public async Task ReportNeverExtendsWarRegionMembershipBounds()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 18, 0, 0, TimeSpan.Zero);

        await fixture.CreateBaseContextAsync(
            start,
            "war-report-bounds",
            "DeadLandsHex");

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "report-bounds",
            """{"totalEnlistments":10,"colonialCasualties":20,"wardenCasualties":30,"dayOfWar":5}""",
            start.AddHours(2));

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.Canonical);
        Assert.Equal(
            start.AddMinutes(1),
            result.Canonical.WarRegion.FirstSeenAt);
        Assert.Equal(
            start.AddMinutes(1),
            result.Canonical.WarRegion.LastSeenAt);

        var stored = await fixture.ReadWarRegionAsync(
            result.Canonical.WarRegion.Id);
        Assert.Equal(start.AddMinutes(1), stored.FirstSeenAt);
        Assert.Equal(start.AddMinutes(1), stored.LastSeenAt);
    }

    [Fact]
    public async Task MapAbsentFromLatestListDefersReportWithoutBurningIdentity()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 19, 0, 0, TimeSpan.Zero);

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-report-map-absent-war",
            """{"warId":"war-map-absent","warNumber":129,"winner":"NONE"}""",
            start);
        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "war-report-map-absent-maps",
            """["MarbanHollow"]""",
            start.AddMinutes(1));

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "war-report-map-absent-report",
            """{"totalEnlistments":10,"colonialCasualties":20,"wardenCasualties":30,"dayOfWar":5}""",
            start.AddMinutes(2));

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Deferred,
            result.Status);
        Assert.Equal(
            "map_membership_unconfirmed",
            result.DeferredReason);
        Assert.Null(result.NormalizationRun);
        Assert.Equal(
            0L,
            await fixture.CountNormalizationRunsAsync(
                WarApiVersions.WarReportNormalizer));
        Assert.Equal(
            0L,
            await fixture.CountAsync("runtime.war_report_observations"));
    }

    [Fact]
    public async Task OldMapRepresentationDoesNotBindReportToNewWar()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 20, 0, 0, TimeSpan.Zero);

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-report-old-war",
            """{"warId":"old-war","warNumber":129,"winner":"WARDENS"}""",
            start);
        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "war-report-old-maps",
            """["DeadLandsHex"]""",
            start.AddMinutes(1));
        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-report-new-war",
            """{"warId":"new-war","warNumber":130,"winner":"NONE"}""",
            start.AddMinutes(10));

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "war-report-new-war-report",
            """{"totalEnlistments":10,"colonialCasualties":20,"wardenCasualties":30,"dayOfWar":0}""",
            start.AddMinutes(11));

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Deferred,
            result.Status);
        Assert.Equal(
            "map_war_context_mismatch",
            result.DeferredReason);
        Assert.Equal(
            0L,
            await fixture.CountAsync("runtime.war_report_observations"));
    }

    [Fact]
    public async Task CrossWarMap304DefersToCoverageInsteadOfInventingMembership()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 21, 0, 0, TimeSpan.Zero);

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-report-304-old-war",
            """{"warId":"old-war-304","warNumber":129,"winner":"WARDENS"}""",
            start);
        var maps = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "war-report-304-maps",
            """["DeadLandsHex"]""",
            start.AddMinutes(1));
        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-report-304-new-war",
            """{"warId":"new-war-304","warNumber":130,"winner":"NONE"}""",
            start.AddMinutes(10));

        await fixture.CreateValidation304Async(
            "live-1",
            WarApiCatalog.Maps(),
            "war-report-304-validation",
            maps.RepresentationFetchId,
            start.AddMinutes(10).AddSeconds(30));

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "war-report-304-report",
            """{"totalEnlistments":10,"colonialCasualties":20,"wardenCasualties":30,"dayOfWar":0}""",
            start.AddMinutes(11));

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Deferred,
            result.Status);
        Assert.Equal(
            "map_continuity_requires_coverage",
            result.DeferredReason);
        Assert.Equal(
            0L,
            await fixture.CountAsync("runtime.war_report_observations"));

        var parseRunsBeforeRecovery =
            await fixture.CountAsync("evidence.source_parse_runs");

        var recovery = await fixture.CoverageRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        Assert.True(recovery.CoverageRecorded >= 4);
        Assert.Equal(1, recovery.ContinuityApplied);
        Assert.Equal(
            1L,
            await fixture.CountCoverageStateAsync("source_not_modified"));
        Assert.Equal(
            parseRunsBeforeRecovery,
            await fixture.CountAsync("evidence.source_parse_runs"));
        Assert.Equal(
            1L,
            await fixture.CountNormalizationRunsAsync(
                WarApiVersions.RegionNormalizer));

        var replay = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Normalized,
            replay.Status);
        Assert.NotNull(replay.Canonical);
        Assert.Equal(
            start.AddMinutes(10).AddSeconds(30),
            replay.Canonical.WarRegion.FirstSeenAt);
        Assert.Equal(
            1L,
            await fixture.CountAsync("runtime.war_report_observations"));
        Assert.Equal(
            1L,
            await fixture.CountAsync("evidence.coverage_reprocessing_runs"));

        var recoveryReplay =
            await fixture.CoverageRecovery.RunOnceAsync(
                TestContext.Current.CancellationToken);

        Assert.Equal(0, recoveryReplay.ContinuityApplied);
        Assert.Equal(
            1L,
            await fixture.CountAsync("evidence.coverage_reprocessing_runs"));
    }

    [Fact]
    public async Task ExactCaseSensitiveMapIdentitySelectsMatchingWarRegion()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 22, 0, 0, TimeSpan.Zero);

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-report-case-war",
            """{"warId":"case-war","warNumber":129,"winner":"NONE"}""",
            start);
        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "war-report-case-maps",
            """["DeadLandsHex","deadlandshex"]""",
            start.AddMinutes(1));

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("deadlandshex"),
            "war-report-case-report",
            """{"totalEnlistments":10,"colonialCasualties":20,"wardenCasualties":30,"dayOfWar":5}""",
            start.AddMinutes(2));

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Normalized,
            result.Status);
        Assert.NotNull(result.Canonical);
        Assert.Equal(
            "deadlandshex",
            result.Canonical.WarRegion.SourceMapName);
        Assert.Equal(
            2L,
            await fixture.CountAsync("runtime.war_regions"));
    }

    [Fact]
    public async Task LatestWarAndMapParsesCanBeNormalizedOnDemand()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 27, 23, 0, 0, TimeSpan.Zero);

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "war-report-on-demand-war",
            """{"warId":"on-demand-war","warNumber":130,"winner":"NONE"}""",
            start);
        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "war-report-on-demand-maps",
            """["DeadLandsHex"]""",
            start.AddMinutes(1));

        Assert.Equal(0L, await fixture.CountAsync("runtime.wars"));
        Assert.Equal(0L, await fixture.CountAsync("runtime.war_regions"));

        var report = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "war-report-on-demand-report",
            """{"totalEnlistments":10,"colonialCasualties":20,"wardenCasualties":30,"dayOfWar":0}""",
            start.AddMinutes(2));

        var result = await fixture.ReportNormalization.NormalizeAsync(
            report.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            WarApiWarReportNormalizationStatus.Normalized,
            result.Status);
        Assert.Equal(1L, await fixture.CountAsync("runtime.wars"));
        Assert.Equal(1L, await fixture.CountAsync("runtime.war_regions"));
        Assert.Equal(
            1L,
            await fixture.CountAsync("runtime.war_report_observations"));
    }

    [Fact]
    public async Task CoverageRecoveryRepairsMissingParseAndCanonicalWorkWithoutNewFetch()
    {
        await using var fixture = await CreateFixtureAsync();
        var observedAt = new DateTimeOffset(
            2026, 9, 28, 0, 0, 0, TimeSpan.Zero);

        _ = await fixture.CreateRawAsync(
            "live-1",
            WarApiCatalog.War(),
            "coverage-raw-war",
            """{"warId":"coverage-war","warNumber":131,"winner":"NONE"}""",
            observedAt);

        Assert.Equal(
            0L,
            await fixture.CountAsync("evidence.source_parse_runs"));
        Assert.Equal(
            0L,
            await fixture.CountAsync("runtime.wars"));
        var fetchCount =
            await fixture.CountAsync("evidence.fetches");

        var result = await fixture.CoverageRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ParseRunsRepaired);
        Assert.Equal(1, result.CoverageRecorded);
        Assert.Equal(1, result.CanonicalCompleted);
        Assert.Equal(
            fetchCount,
            await fixture.CountAsync("evidence.fetches"));
        Assert.Equal(
            1L,
            await fixture.CountAsync("evidence.source_parse_runs"));
        Assert.Equal(
            1L,
            await fixture.CountAsync("runtime.wars"));
        Assert.Equal(
            1L,
            await fixture.CountCoverageStateAsync("observed"));

        var replay = await fixture.CoverageRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(0, replay.CoverageRecorded);
        Assert.Equal(0, replay.ParseRunsRepaired);
        Assert.Equal(0, replay.CanonicalCompleted);
        Assert.Equal(
            1L,
            await fixture.CountCoverageStateAsync("observed"));
    }

    [Fact]
    public async Task CoverageRecoveryClassifiesSourceCollectorAndUncertainGaps()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 28, 1, 0, 0, TimeSpan.Zero);

        await fixture.CreateHttpStatusAsync(
            "live-1",
            WarApiCatalog.War(),
            "coverage-source-unavailable",
            503,
            start);

        await fixture.CreateDeferredAttemptAsync(
            "live-1",
            WarApiCatalog.War(),
            "coverage-collector-unavailable",
            authorizeExchange: false,
            start.AddMinutes(1));

        await fixture.CreateDeferredAttemptAsync(
            "live-1",
            WarApiCatalog.War(),
            "coverage-uncertain",
            authorizeExchange: true,
            start.AddMinutes(2));

        var result = await fixture.CoverageRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.CoverageRecorded);
        Assert.Equal(
            1L,
            await fixture.CountCoverageStateAsync("source_unavailable"));
        Assert.Equal(
            1L,
            await fixture.CountCoverageStateAsync("collector_unavailable"));
        Assert.Equal(
            1L,
            await fixture.CountCoverageStateAsync("unknown"));
        Assert.Equal(
            0L,
            await fixture.CountAsync("runtime.wars"));
    }

    [Fact]
    public async Task CoverageRecoveryClassifiesMalformedRepresentationAsRejected()
    {
        await using var fixture = await CreateFixtureAsync();
        var observedAt = new DateTimeOffset(
            2026, 9, 28, 2, 0, 0, TimeSpan.Zero);

        _ = await fixture.CreateRawAsync(
            "live-1",
            WarApiCatalog.War(),
            "coverage-malformed-war",
            """{"warId":""",
            observedAt);

        var result = await fixture.CoverageRecovery.RunOnceAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ParseRunsRepaired);
        Assert.Equal(1, result.CoverageRecorded);
        Assert.Equal(1, result.CanonicalCompleted);
        Assert.Equal(
            1L,
            await fixture.CountCoverageStateAsync("rejected"));
        Assert.Equal(
            1L,
            await fixture.CountNormalizationOutcomeAsync(
                WarApiVersions.WarNormalizer,
                "rejected"));
        Assert.Equal(
            0L,
            await fixture.CountAsync("runtime.wars"));
    }

    [Fact]
    public async Task M5CompletionGateRebuildsEquivalentCanonicalGraphFromDurableEvidence()
    {
        await using var fixture = await CreateFixtureAsync();
        var start = new DateTimeOffset(
            2026, 9, 28, 3, 0, 0, TimeSpan.Zero);

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "completion-old-war",
            """{"warId":"completion-old-war","warNumber":131,"winner":"WARDENS"}""",
            start);

        var maps = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "completion-maps",
            """["DeadLandsHex","MarbanHollow"]""",
            start.AddMinutes(1));

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.War(),
            "completion-new-war",
            """{"warId":"completion-new-war","warNumber":132,"winner":"NONE"}""",
            start.AddMinutes(10));

        await fixture.CreateValidation304Async(
            "live-1",
            WarApiCatalog.Maps(),
            "completion-maps-304",
            maps.RepresentationFetchId,
            start.AddMinutes(10).AddSeconds(30));

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.WarReport("DeadLandsHex"),
            "completion-report",
            """{"totalEnlistments":40,"colonialCasualties":50,"wardenCasualties":60,"dayOfWar":0}""",
            start.AddMinutes(11));

        await fixture.CreateHttpStatusAsync(
            "live-1",
            WarApiCatalog.War(),
            "completion-source-unavailable",
            503,
            start.AddMinutes(20));
        await fixture.CreateDeferredAttemptAsync(
            "live-1",
            WarApiCatalog.War(),
            "completion-collector-unavailable",
            authorizeExchange: false,
            start.AddMinutes(21));
        await fixture.CreateDeferredAttemptAsync(
            "live-1",
            WarApiCatalog.War(),
            "completion-uncertain",
            authorizeExchange: true,
            start.AddMinutes(22));
        _ = await fixture.CreateRawAsync(
            "live-1",
            WarApiCatalog.War(),
            "completion-rejected-war",
            """{"warId":""",
            start.AddMinutes(23));

        var fetchCountBeforeRecovery =
            await fixture.CountAsync("evidence.fetches");
        var payloadCountBeforeRecovery =
            await fixture.CountAsync("evidence.payloads");

        await fixture.RunRecoveryUntilQuiescentAsync();

        Assert.Equal(
            fetchCountBeforeRecovery,
            await fixture.CountAsync("evidence.fetches"));
        Assert.Equal(
            payloadCountBeforeRecovery,
            await fixture.CountAsync("evidence.payloads"));

        var fetchCount = fetchCountBeforeRecovery;
        var payloadCount = payloadCountBeforeRecovery;
        var parseCount =
            await fixture.CountAsync("evidence.source_parse_runs");

        Assert.True(
            await fixture.CountCoverageStateAsync("observed") > 0);
        Assert.True(
            await fixture.CountCoverageStateAsync("source_not_modified") > 0);
        Assert.Equal(
            1L,
            await fixture.CountCoverageStateAsync("source_unavailable"));
        Assert.Equal(
            1L,
            await fixture.CountCoverageStateAsync("collector_unavailable"));
        Assert.Equal(
            1L,
            await fixture.CountCoverageStateAsync("unknown"));
        Assert.Equal(
            1L,
            await fixture.CountCoverageStateAsync("rejected"));

        Assert.Equal(
            0L,
            await fixture.CountM5ProvenanceViolationsAsync());
        Assert.Equal(
            0L,
            await fixture.CountM5OutstandingWorkAsync());
        Assert.Equal(
            0L,
            await fixture.CountM5ProjectionViolationsAsync());

        var first = await fixture.ReadM5SemanticSnapshotAsync();

        Assert.Equal(2L, await fixture.CountAsync("runtime.wars"));
        Assert.Equal(4L, await fixture.CountAsync("runtime.war_regions"));
        Assert.Equal(
            1L,
            await fixture.CountAsync("runtime.war_report_observations"));
        Assert.Equal(
            1L,
            await fixture.CountAsync("evidence.coverage_reprocessing_runs"));

        await fixture.DeleteM5DerivedStateAsync();

        Assert.Equal(
            fetchCount,
            await fixture.CountAsync("evidence.fetches"));
        Assert.Equal(
            payloadCount,
            await fixture.CountAsync("evidence.payloads"));
        Assert.Equal(
            parseCount,
            await fixture.CountAsync("evidence.source_parse_runs"));
        Assert.Equal(0L, await fixture.CountAsync("runtime.wars"));
        Assert.Equal(
            0L,
            await fixture.CountAsync("evidence.normalization_runs"));
        Assert.Equal(
            0L,
            await fixture.CountAsync("evidence.coverage_observations"));

        await fixture.RunRecoveryUntilQuiescentAsync();

        Assert.Equal(
            fetchCount,
            await fixture.CountAsync("evidence.fetches"));
        Assert.Equal(
            payloadCount,
            await fixture.CountAsync("evidence.payloads"));
        Assert.Equal(
            parseCount,
            await fixture.CountAsync("evidence.source_parse_runs"));
        Assert.Equal(
            0L,
            await fixture.CountM5ProvenanceViolationsAsync());
        Assert.Equal(
            0L,
            await fixture.CountM5OutstandingWorkAsync());
        Assert.Equal(
            0L,
            await fixture.CountM5ProjectionViolationsAsync());

        var rebuilt = await fixture.ReadM5SemanticSnapshotAsync();

        Assert.Equal(first, rebuilt);
    }

    [Fact]
    public async Task M5CompletionGateDoesNotTreatDeferredCanonicalWorkAsQuiescent()
    {
        await using var fixture = await CreateFixtureAsync();
        var observedAt = new DateTimeOffset(
            2026, 9, 28, 5, 0, 0, TimeSpan.Zero);

        _ = await fixture.CreateParsedAsync(
            "live-1",
            WarApiCatalog.Maps(),
            "completion-deferred-maps",
            """["DeadLandsHex"]""",
            observedAt);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.RunRecoveryUntilQuiescentAsync());

        Assert.Contains(
            "deferred",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(
            await fixture.CountM5OutstandingWorkAsync() > 0);
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        await MigrateAsync();
        await ResetAsync();

        var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        var registry = new SourceRegistry(
            new PostgresSourceRegistryStore(dataSource));
        var source = await registry.RegisterSourceAsync(
            WarApiCatalog.SourceKey,
            "Official Foxhole War API",
            TestContext.Current.CancellationToken);

        var normalization = new NormalizationKernel(
            new PostgresNormalizationRunStore(dataSource));
        var canonicalEvidence =
            new PostgresCanonicalEvidenceReader(dataSource);
        var options = CreateOptions();
        var sourceContextReader =
            new PostgresWarContextReader(dataSource);
        var warNormalization =
            new WarApiWarNormalizationCoordinator(
                canonicalEvidence,
                new WarCanonicalKernel(
                    new PostgresWarCanonicalStore(dataSource)),
                normalization,
                options,
                TimeProvider.System);
        var regionNormalization =
            new WarApiRegionNormalizationCoordinator(
                canonicalEvidence,
                sourceContextReader,
                warNormalization,
                new RegionCanonicalKernel(
                    new PostgresRegionCanonicalStore(dataSource)),
                normalization,
                options,
                TimeProvider.System);
        var coverageStore = new PostgresCoverageStore(dataSource);
        var reportNormalization =
            new WarApiWarReportNormalizationCoordinator(
                canonicalEvidence,
                sourceContextReader,
                new PostgresWarRegionReader(dataSource),
                coverageStore,
                warNormalization,
                regionNormalization,
                new WarReportCanonicalKernel(
                    new PostgresWarReportCanonicalStore(dataSource)),
                normalization,
                options,
                TimeProvider.System);
        var coverageRecovery =
            new WarApiCoverageRecoveryCoordinator(
                coverageStore,
                new PostgresSourceParseRunStore(dataSource),
                sourceContextReader,
                warNormalization,
                regionNormalization,
                reportNormalization,
                options,
                TimeProvider.System);

        return new Fixture(
            dataSource,
            registry,
            source.Resource.Id,
            new IngestionKernel(
                new PostgresIngestionKernelStore(dataSource)),
            new EvidenceKernel(
                new PostgresEvidenceKernelStore(dataSource)),
            new PostgresSourceParseRunStore(dataSource),
            reportNormalization,
            coverageRecovery);
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
                runtime.war_report_observations,
                runtime.war_observations,
                runtime.war_regions,
                runtime.regions,
                runtime.wars,
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

    private static WarApiWorkerOptions CreateOptions() =>
        new(
            Enabled: false,
            Shards: [WarApiShard.Live1],
            LeaseDuration: TimeSpan.FromMinutes(2),
            IdleDelay: TimeSpan.FromMilliseconds(10),
            RecoveryInterval: TimeSpan.FromMilliseconds(10),
            PlannerInterval: TimeSpan.FromMilliseconds(10),
            PlannerBatchSize: 64,
            ConnectTimeout: TimeSpan.FromSeconds(5),
            ExchangeTimeout: TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer: 4,
            MaxResponseHeadersLengthKiB: 16,
            MaxWireBytes: 1024 * 1024,
            MaxDecodedBytes: 2 * 1024 * 1024,
            MaxExpansionRatio: 20,
            OutboundGlobalMinimumInterval:
                TimeSpan.FromMilliseconds(150),
            OutboundPerHostMinimumInterval:
                TimeSpan.FromMilliseconds(400),
            UserAgent: "FoxData-Test/M5-F");

    private sealed class Fixture(
        NpgsqlDataSource dataSource,
        SourceRegistry registry,
        FoxData.Core.Sources.SourceId sourceId,
        IngestionKernel ingestion,
        EvidenceKernel evidence,
        ISourceParseRunStore parseRuns,
        WarApiWarReportNormalizationCoordinator reportNormalization,
        WarApiCoverageRecoveryCoordinator coverageRecovery)
        : IAsyncDisposable
    {
        public WarApiWarReportNormalizationCoordinator ReportNormalization { get; } =
            reportNormalization;

        public WarApiCoverageRecoveryCoordinator CoverageRecovery { get; } =
            coverageRecovery;

        public async Task CreateBaseContextAsync(
            DateTimeOffset start,
            string keyPrefix,
            string sourceMapName)
        {
            _ = await CreateParsedAsync(
                "live-1",
                WarApiCatalog.War(),
                $"{keyPrefix}-war",
                """{"warId":"base-war","warNumber":129,"winner":"NONE"}""",
                start);
            _ = await CreateParsedAsync(
                "live-1",
                WarApiCatalog.Maps(),
                $"{keyPrefix}-maps",
                $"[\"{sourceMapName}\"]",
                start.AddMinutes(1));
        }

        public async Task<FetchId> CreateRawAsync(
            string shardKey,
            SourceEndpoint sourceEndpoint,
            string idempotencyKey,
            string json,
            DateTimeOffset retrievedAt)
        {
            var shard = await registry.RegisterShardAsync(
                sourceId,
                shardKey,
                shardKey,
                "live",
                TestContext.Current.CancellationToken);
            var endpoint = await registry.RegisterEndpointAsync(
                shard.Resource.Id,
                sourceEndpoint.Capability.Key,
                sourceEndpoint.SemanticKey,
                TestContext.Current.CancellationToken);

            var capture = await CaptureAsync(
                endpoint.Resource.Id,
                idempotencyKey,
                retrievedAt,
                statusCode: 200,
                Encoding.UTF8.GetBytes(json),
                priorFetchId: null);

            return capture.Fetch!.Id;
        }

        public async Task CreateHttpStatusAsync(
            string shardKey,
            SourceEndpoint sourceEndpoint,
            string idempotencyKey,
            int statusCode,
            DateTimeOffset retrievedAt)
        {
            var shard = await registry.RegisterShardAsync(
                sourceId,
                shardKey,
                shardKey,
                "live",
                TestContext.Current.CancellationToken);
            var endpoint = await registry.RegisterEndpointAsync(
                shard.Resource.Id,
                sourceEndpoint.Capability.Key,
                sourceEndpoint.SemanticKey,
                TestContext.Current.CancellationToken);

            _ = await CaptureAsync(
                endpoint.Resource.Id,
                idempotencyKey,
                retrievedAt,
                statusCode,
                body: null,
                priorFetchId: null);
        }

        public async Task CreateDeferredAttemptAsync(
            string shardKey,
            SourceEndpoint sourceEndpoint,
            string idempotencyKey,
            bool authorizeExchange,
            DateTimeOffset observedAt)
        {
            var shard = await registry.RegisterShardAsync(
                sourceId,
                shardKey,
                shardKey,
                "live",
                TestContext.Current.CancellationToken);
            var endpoint = await registry.RegisterEndpointAsync(
                shard.Resource.Id,
                sourceEndpoint.Capability.Key,
                sourceEndpoint.SemanticKey,
                TestContext.Current.CancellationToken);

            var scheduledAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            var queued = await ingestion.EnqueueAsync(
                endpoint.Resource.Id,
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

            if (authorizeExchange)
            {
                var authorized = await ingestion.AuthorizeExchangeAsync(
                    attemptId,
                    workerId,
                    claim.Job.LeaseGeneration,
                    TestContext.Current.CancellationToken);
                Assert.Equal(
                    ExchangeAuthorizationStatus.AuthorizedNow,
                    authorized.Status);

                var deferred = await ingestion.DeferUncertainExchangeAsync(
                    attemptId,
                    workerId,
                    claim.Job.LeaseGeneration,
                    observedAt,
                    "test",
                    "test_uncertain",
                    TestContext.Current.CancellationToken);
                Assert.Equal(
                    AttemptDeferralStatus.DeferredNow,
                    deferred.Status);
            }
            else
            {
                var deferred = await ingestion.DeferBeforeExchangeAsync(
                    attemptId,
                    workerId,
                    claim.Job.LeaseGeneration,
                    observedAt,
                    "test",
                    "test_collector_unavailable",
                    TestContext.Current.CancellationToken);
                Assert.Equal(
                    AttemptDeferralStatus.DeferredNow,
                    deferred.Status);
            }
        }

        public async Task<SourceParseRunDescriptor> CreateParsedAsync(
            string shardKey,
            SourceEndpoint sourceEndpoint,
            string idempotencyKey,
            string json,
            DateTimeOffset retrievedAt)
        {
            var shard = await registry.RegisterShardAsync(
                sourceId,
                shardKey,
                shardKey,
                "live",
                TestContext.Current.CancellationToken);
            var endpoint = await registry.RegisterEndpointAsync(
                shard.Resource.Id,
                sourceEndpoint.Capability.Key,
                sourceEndpoint.SemanticKey,
                TestContext.Current.CancellationToken);

            var capture = await CaptureAsync(
                endpoint.Resource.Id,
                idempotencyKey,
                retrievedAt,
                statusCode: 200,
                Encoding.UTF8.GetBytes(json),
                priorFetchId: null);

            var body = capture.Payload!.Body.ToArray();
            var parsed = new WarApiParser().Parse(
                sourceEndpoint.Capability,
                body);
            Assert.True(parsed.Parsed);

            var parseStartedAt =
                retrievedAt.AddMilliseconds(1);
            return await parseRuns.RecordAsync(
                new SourceParseRunWrite(
                    capture.Fetch!.Id,
                    sourceEndpoint.Capability.Key,
                    WarApiVersions.Adapter,
                    WarApiVersions.Parser,
                    JsonStructuralFingerprinter.Algorithm,
                    parsed.StructuralFingerprint,
                    parsed.Outcome == WarApiParseOutcome.Parsed
                        ? "parsed"
                        : "parsed_with_unknowns",
                    parsed.UnknownPropertyCount,
                    parsed.UnknownCodeCount,
                    parsed.ErrorCode,
                    parseStartedAt,
                    parseStartedAt.AddMilliseconds(1),
                    parsed.SourceVersion,
                    parsed.SourceLastUpdated,
                    body.LongLength),
                TestContext.Current.CancellationToken);
        }

        public async Task CreateValidation304Async(
            string shardKey,
            SourceEndpoint sourceEndpoint,
            string idempotencyKey,
            FetchId priorFetchId,
            DateTimeOffset retrievedAt)
        {
            var shard = await registry.RegisterShardAsync(
                sourceId,
                shardKey,
                shardKey,
                "live",
                TestContext.Current.CancellationToken);
            var endpoint = await registry.RegisterEndpointAsync(
                shard.Resource.Id,
                sourceEndpoint.Capability.Key,
                sourceEndpoint.SemanticKey,
                TestContext.Current.CancellationToken);

            var capture = await CaptureAsync(
                endpoint.Resource.Id,
                idempotencyKey,
                retrievedAt,
                statusCode: 304,
                body: null,
                priorFetchId);

            Assert.Equal(
                CaptureStatus.CapturedCurrent,
                capture.Status);
            Assert.Null(capture.Fetch!.PayloadId);
            Assert.Equal(
                priorFetchId,
                capture.Fetch.PriorFetchId);
        }

        private async Task<CaptureResult> CaptureAsync(
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
            Assert.Equal(
                JobEnqueueStatus.Created,
                queued.Status);

            var workerId = WorkerInstanceId.New();
            var claim = await ingestion.ClaimNextAsync(
                workerId,
                TimeSpan.FromMinutes(5),
                TestContext.Current.CancellationToken);
            Assert.True(claim.Claimed);
            Assert.Equal(
                endpointId,
                claim.Job!.EndpointId);

            var attemptId = IngestionAttemptId.New();
            var begun = await ingestion.BeginAttemptAsync(
                attemptId,
                claim.Job.Id,
                workerId,
                claim.Job.LeaseGeneration,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                BeginAttemptStatus.Started,
                begun.Status);

            var fenced =
                await ingestion.AcquireEndpointFenceAsync(
                    attemptId,
                    workerId,
                    claim.Job.LeaseGeneration,
                    TestContext.Current.CancellationToken);
            Assert.Equal(
                FenceAcquireStatus.AcquiredNow,
                fenced.Status);

            var authorized =
                await ingestion.AuthorizeExchangeAsync(
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

            var capture =
                await evidence.CaptureSourceResponseAsync(
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
                        "\"m5-f\"",
                        "max-age=60",
                        retrievedAt.AddMinutes(1),
                        2),
                    capturedBody,
                    priorFetchId,
                    TestContext.Current.CancellationToken);

            Assert.Equal(
                CaptureStatus.CapturedCurrent,
                capture.Status);

            return capture;
        }

        public async Task<WarRegionDescriptor> ReadWarRegionAsync(
            WarRegionId warRegionId)
        {
            await using var command =
                dataSource.CreateCommand(
                    """
                    SELECT
                        id, war_id, region_id,
                        source_map_name, source_region_id,
                        first_seen_at, last_seen_at, created_at
                    FROM runtime.war_regions
                    WHERE id = @id;
                    """);
            command.Parameters.AddWithValue(
                "id",
                warRegionId.Value);

            await using var reader =
                await command.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
            Assert.True(
                await reader.ReadAsync(
                    TestContext.Current.CancellationToken));

            return new WarRegionDescriptor(
                new WarRegionId(reader.GetGuid(0)),
                new WarId(reader.GetGuid(1)),
                new RegionId(reader.GetGuid(2)),
                reader.GetString(3),
                reader.IsDBNull(4)
                    ? null
                    : reader.GetInt32(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7));
        }

        public async Task RunRecoveryUntilQuiescentAsync()
        {
            for (var pass = 0; pass < 8; pass++)
            {
                var result = await CoverageRecovery.RunOnceAsync(
                    TestContext.Current.CancellationToken);

                if (result.ProgressCount == 0)
                {
                    if (result.CanonicalDeferred > 0)
                    {
                        throw new InvalidOperationException(
                            $"M5 recovery made no progress with {result.CanonicalDeferred} deferred canonical item(s) still unresolved.");
                    }

                    return;
                }
            }

            throw new InvalidOperationException(
                "M5 recovery did not reach a quiescent state within eight deterministic passes.");
        }

        public async Task DeleteM5DerivedStateAsync()
        {
            await using var command =
                dataSource.CreateCommand(
                    """
                    DELETE FROM runtime.war_report_observations;
                    DELETE FROM runtime.war_observations;
                    DELETE FROM runtime.war_regions;
                    DELETE FROM runtime.regions;
                    DELETE FROM runtime.wars;
                    DELETE FROM evidence.coverage_reprocessing_runs;
                    DELETE FROM evidence.coverage_observations;
                    DELETE FROM evidence.normalization_runs;
                    """);

            await command.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
        }

        public async Task<string> ReadM5SemanticSnapshotAsync()
        {
            await using var command =
                dataSource.CreateCommand(
                    """
                    SELECT jsonb_build_object(
                        'wars',
                        COALESCE(
                            (
                                SELECT jsonb_agg(
                                    jsonb_build_object(
                                        'shard', shard.key,
                                        'sourceWarId', war.source_war_id,
                                        'warNumber', war.war_number,
                                        'firstObservedAt', war.first_observed_at,
                                        'lastObservedAt', war.last_observed_at)
                                    ORDER BY shard.key, war.source_war_id)
                                FROM runtime.wars AS war
                                INNER JOIN sources.shards AS shard
                                    ON shard.id = war.shard_id
                            ),
                            '[]'::jsonb),
                        'warObservations',
                        COALESCE(
                            (
                                SELECT jsonb_agg(
                                    jsonb_build_object(
                                        'shard', shard.key,
                                        'sourceWarId', war.source_war_id,
                                        'sourceParseRunId', normalization.source_parse_run_id,
                                        'normalizerVersion', normalization.normalizer_version,
                                        'representationFetchId', observation.representation_fetch_id,
                                        'observedAt', observation.observed_at,
                                        'warNumber', observation.war_number,
                                        'winner', observation.winner,
                                        'conquestStartTime', observation.conquest_start_time,
                                        'conquestEndTime', observation.conquest_end_time,
                                        'resistanceStartTime', observation.resistance_start_time,
                                        'scheduledConquestEndTime', observation.scheduled_conquest_end_time,
                                        'requiredVictoryTowns', observation.required_victory_towns,
                                        'shortRequiredVictoryTowns', observation.short_required_victory_towns)
                                    ORDER BY
                                        shard.key,
                                        war.source_war_id,
                                        observation.observed_at,
                                        observation.representation_fetch_id)
                                FROM runtime.war_observations AS observation
                                INNER JOIN runtime.wars AS war
                                    ON war.id = observation.war_id
                                INNER JOIN sources.shards AS shard
                                    ON shard.id = war.shard_id
                                INNER JOIN evidence.normalization_runs AS normalization
                                    ON normalization.id = observation.normalization_run_id
                            ),
                            '[]'::jsonb),
                        'regions',
                        COALESCE(
                            (
                                SELECT jsonb_agg(
                                    jsonb_build_object(
                                        'canonicalKey', region.canonical_key,
                                        'displayName', region.display_name)
                                    ORDER BY region.canonical_key)
                                FROM runtime.regions AS region
                            ),
                            '[]'::jsonb),
                        'warRegions',
                        COALESCE(
                            (
                                SELECT jsonb_agg(
                                    jsonb_build_object(
                                        'shard', shard.key,
                                        'sourceWarId', war.source_war_id,
                                        'canonicalKey', region.canonical_key,
                                        'sourceMapName', membership.source_map_name,
                                        'sourceRegionId', membership.source_region_id,
                                        'firstSeenAt', membership.first_seen_at,
                                        'lastSeenAt', membership.last_seen_at)
                                    ORDER BY
                                        shard.key,
                                        war.source_war_id,
                                        membership.source_map_name)
                                FROM runtime.war_regions AS membership
                                INNER JOIN runtime.wars AS war
                                    ON war.id = membership.war_id
                                INNER JOIN sources.shards AS shard
                                    ON shard.id = war.shard_id
                                INNER JOIN runtime.regions AS region
                                    ON region.id = membership.region_id
                            ),
                            '[]'::jsonb),
                        'warReports',
                        COALESCE(
                            (
                                SELECT jsonb_agg(
                                    jsonb_build_object(
                                        'shard', shard.key,
                                        'sourceWarId', war.source_war_id,
                                        'sourceMapName', membership.source_map_name,
                                        'sourceParseRunId', normalization.source_parse_run_id,
                                        'normalizerVersion', normalization.normalizer_version,
                                        'representationFetchId', observation.representation_fetch_id,
                                        'observedAt', observation.observed_at,
                                        'totalEnlistments', observation.total_enlistments,
                                        'colonialCasualties', observation.colonial_casualties,
                                        'wardenCasualties', observation.warden_casualties,
                                        'dayOfWar', observation.day_of_war)
                                    ORDER BY
                                        shard.key,
                                        war.source_war_id,
                                        membership.source_map_name,
                                        observation.observed_at,
                                        observation.representation_fetch_id)
                                FROM runtime.war_report_observations AS observation
                                INNER JOIN runtime.war_regions AS membership
                                    ON membership.id = observation.war_region_id
                                INNER JOIN runtime.wars AS war
                                    ON war.id = membership.war_id
                                INNER JOIN sources.shards AS shard
                                    ON shard.id = war.shard_id
                                INNER JOIN evidence.normalization_runs AS normalization
                                    ON normalization.id = observation.normalization_run_id
                            ),
                            '[]'::jsonb),
                        'normalizationRuns',
                        COALESCE(
                            (
                                SELECT jsonb_agg(
                                    jsonb_build_object(
                                        'sourceParseRunId', normalization.source_parse_run_id,
                                        'normalizerVersion', normalization.normalizer_version,
                                        'outcome', normalization.outcome,
                                        'errorCode', normalization.error_code)
                                    ORDER BY
                                        normalization.source_parse_run_id,
                                        normalization.normalizer_version)
                                FROM evidence.normalization_runs AS normalization
                            ),
                            '[]'::jsonb),
                        'coverage',
                        COALESCE(
                            (
                                SELECT jsonb_agg(
                                    jsonb_build_object(
                                        'attemptId', coverage.attempt_id,
                                        'validationFetchId', coverage.validation_fetch_id,
                                        'representationFetchId', coverage.representation_fetch_id,
                                        'sourceParseRunId', coverage.source_parse_run_id,
                                        'state', coverage.state,
                                        'boundaryAt', coverage.boundary_at,
                                        'detailCode', coverage.detail_code)
                                    ORDER BY coverage.attempt_id)
                                FROM evidence.coverage_observations AS coverage
                            ),
                            '[]'::jsonb),
                        'coverageReprocessing',
                        COALESCE(
                            (
                                SELECT jsonb_agg(
                                    jsonb_build_object(
                                        'attemptId', coverage.attempt_id,
                                        'processorVersion', run.processor_version,
                                        'outcome', run.outcome,
                                        'errorCode', run.error_code)
                                    ORDER BY
                                        coverage.attempt_id,
                                        run.processor_version)
                                FROM evidence.coverage_reprocessing_runs AS run
                                INNER JOIN evidence.coverage_observations AS coverage
                                    ON coverage.id = run.coverage_observation_id
                            ),
                            '[]'::jsonb)
                    )::text;
                    """);

            return (string)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public async Task<long> CountM5ProvenanceViolationsAsync()
        {
            await using var command =
                dataSource.CreateCommand(
                    """
                    SELECT
                        (
                            SELECT COUNT(*)
                            FROM runtime.war_observations AS observation
                            LEFT JOIN evidence.normalization_runs AS normalization
                                ON normalization.id = observation.normalization_run_id
                            LEFT JOIN evidence.source_parse_runs AS parse_run
                                ON parse_run.id = normalization.source_parse_run_id
                            LEFT JOIN evidence.fetches AS representation_fetch
                                ON representation_fetch.id = parse_run.representation_fetch_id
                            LEFT JOIN evidence.payloads AS payload
                                ON payload.id = representation_fetch.payload_id
                            LEFT JOIN sources.endpoints AS endpoint
                                ON endpoint.id = representation_fetch.endpoint_id
                            WHERE normalization.id IS NULL
                               OR normalization.outcome <> 'normalized'
                               OR normalization.normalizer_version <> 'warapi-war-normalizer@1'
                               OR parse_run.id IS NULL
                               OR parse_run.capability_key <> 'runtime-war-state'
                               OR representation_fetch.id IS NULL
                               OR payload.id IS NULL
                               OR endpoint.id IS NULL
                               OR endpoint.capability_key <> 'runtime-war-state'
                               OR endpoint.semantic_key <> 'war'
                               OR observation.representation_fetch_id <> parse_run.representation_fetch_id
                        )
                        +
                        (
                            SELECT COUNT(*)
                            FROM runtime.war_report_observations AS observation
                            INNER JOIN runtime.war_regions AS membership
                                ON membership.id = observation.war_region_id
                            LEFT JOIN evidence.normalization_runs AS normalization
                                ON normalization.id = observation.normalization_run_id
                            LEFT JOIN evidence.source_parse_runs AS parse_run
                                ON parse_run.id = normalization.source_parse_run_id
                            LEFT JOIN evidence.fetches AS representation_fetch
                                ON representation_fetch.id = parse_run.representation_fetch_id
                            LEFT JOIN evidence.payloads AS payload
                                ON payload.id = representation_fetch.payload_id
                            LEFT JOIN sources.endpoints AS endpoint
                                ON endpoint.id = representation_fetch.endpoint_id
                            WHERE normalization.id IS NULL
                               OR normalization.outcome <> 'normalized'
                               OR normalization.normalizer_version <> 'warapi-war-report-normalizer@1'
                               OR parse_run.id IS NULL
                               OR parse_run.capability_key <> 'region-war-report'
                               OR representation_fetch.id IS NULL
                               OR payload.id IS NULL
                               OR endpoint.id IS NULL
                               OR endpoint.capability_key <> 'region-war-report'
                               OR endpoint.semantic_key <> 'war-report/' || membership.source_map_name
                               OR observation.representation_fetch_id <> parse_run.representation_fetch_id
                        )
                        +
                        (
                            SELECT COUNT(*)
                            FROM evidence.normalization_runs AS normalization
                            WHERE normalization.outcome = 'normalized'
                              AND normalization.normalizer_version = 'warapi-war-normalizer@1'
                              AND NOT EXISTS (
                                  SELECT 1
                                  FROM runtime.war_observations AS observation
                                  WHERE observation.normalization_run_id = normalization.id)
                        )
                        +
                        (
                            SELECT COUNT(*)
                            FROM evidence.normalization_runs AS normalization
                            WHERE normalization.outcome = 'normalized'
                              AND normalization.normalizer_version = 'warapi-war-report-normalizer@1'
                              AND NOT EXISTS (
                                  SELECT 1
                                  FROM runtime.war_report_observations AS observation
                                  WHERE observation.normalization_run_id = normalization.id)
                        )
                        +
                        (
                            SELECT COUNT(*)
                            FROM evidence.coverage_observations AS coverage
                            LEFT JOIN sources.endpoints AS endpoint
                                ON endpoint.id = coverage.endpoint_id
                            LEFT JOIN evidence.fetches AS validation_fetch
                                ON validation_fetch.id = coverage.validation_fetch_id
                            LEFT JOIN evidence.fetches AS representation_fetch
                                ON representation_fetch.id = coverage.representation_fetch_id
                            LEFT JOIN evidence.payloads AS payload
                                ON payload.id = representation_fetch.payload_id
                            LEFT JOIN evidence.source_parse_runs AS parse_run
                                ON parse_run.id = coverage.source_parse_run_id
                            WHERE
                                (
                                    coverage.state = 'observed'
                                    AND (
                                        validation_fetch.id IS NULL
                                        OR validation_fetch.id <> representation_fetch.id
                                        OR validation_fetch.endpoint_id <> coverage.endpoint_id
                                        OR representation_fetch.endpoint_id <> coverage.endpoint_id
                                        OR payload.id IS NULL
                                        OR parse_run.id IS NULL
                                        OR parse_run.representation_fetch_id <> representation_fetch.id
                                        OR parse_run.capability_key <> endpoint.capability_key)
                                )
                                OR
                                (
                                    coverage.state = 'source_not_modified'
                                    AND (
                                        validation_fetch.id IS NULL
                                        OR validation_fetch.status_code <> 304
                                        OR validation_fetch.endpoint_id <> coverage.endpoint_id
                                        OR validation_fetch.prior_fetch_id <> representation_fetch.id
                                        OR representation_fetch.endpoint_id <> coverage.endpoint_id
                                        OR payload.id IS NULL
                                        OR parse_run.id IS NULL
                                        OR parse_run.representation_fetch_id <> representation_fetch.id
                                        OR parse_run.capability_key <> endpoint.capability_key)
                                )
                                OR
                                (
                                    coverage.state = 'rejected'
                                    AND (
                                        representation_fetch.id IS NULL
                                        OR payload.id IS NULL
                                        OR parse_run.id IS NULL
                                        OR parse_run.representation_fetch_id <> representation_fetch.id)
                                )
                        )
                        +
                        (
                            SELECT COUNT(*)
                            FROM evidence.coverage_reprocessing_runs AS run
                            LEFT JOIN evidence.coverage_observations AS coverage
                                ON coverage.id = run.coverage_observation_id
                            LEFT JOIN evidence.fetches AS validation_fetch
                                ON validation_fetch.id = coverage.validation_fetch_id
                            LEFT JOIN evidence.fetches AS representation_fetch
                                ON representation_fetch.id = coverage.representation_fetch_id
                            WHERE coverage.id IS NULL
                               OR coverage.state <> 'source_not_modified'
                               OR validation_fetch.status_code <> 304
                               OR validation_fetch.prior_fetch_id <> representation_fetch.id
                        );
                    """);

            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public async Task<long> CountM5OutstandingWorkAsync()
        {
            await using var command =
                dataSource.CreateCommand(
                    """
                    SELECT
                        (
                            SELECT COUNT(*)
                            FROM ingest.attempts AS attempt
                            INNER JOIN ingest.collection_jobs AS job
                                ON job.id = attempt.job_id
                            INNER JOIN sources.endpoints AS endpoint
                                ON endpoint.id = job.endpoint_id
                            INNER JOIN sources.shards AS shard
                                ON shard.id = endpoint.shard_id
                            INNER JOIN sources.sources AS source
                                ON source.id = shard.source_id
                            LEFT JOIN evidence.coverage_observations AS coverage
                                ON coverage.attempt_id = attempt.id
                            WHERE source.key = 'official-war-api'
                              AND endpoint.capability_key IN
                                  ('runtime-war-state', 'active-map-list', 'region-war-report')
                              AND attempt.state IN
                                  ('completed', 'failed', 'uncertain', 'superseded', 'captured_late')
                              AND coverage.id IS NULL
                        )
                        +
                        (
                            SELECT COUNT(*)
                            FROM evidence.source_parse_runs AS parse_run
                            INNER JOIN evidence.fetches AS representation_fetch
                                ON representation_fetch.id = parse_run.representation_fetch_id
                            INNER JOIN ingest.attempts AS attempt
                                ON attempt.id = representation_fetch.attempt_id
                            INNER JOIN sources.endpoints AS endpoint
                                ON endpoint.id = representation_fetch.endpoint_id
                            INNER JOIN sources.shards AS shard
                                ON shard.id = endpoint.shard_id
                            INNER JOIN sources.sources AS source
                                ON source.id = shard.source_id
                            LEFT JOIN evidence.normalization_runs AS normalization
                                ON normalization.source_parse_run_id = parse_run.id
                               AND normalization.normalizer_version =
                                   CASE endpoint.capability_key
                                       WHEN 'runtime-war-state' THEN 'warapi-war-normalizer@1'
                                       WHEN 'active-map-list' THEN 'warapi-region-normalizer@1'
                                       WHEN 'region-war-report' THEN 'warapi-war-report-normalizer@1'
                                       ELSE ''
                                   END
                            WHERE source.key = 'official-war-api'
                              AND parse_run.parser_version = 'warapi-parser@1'
                              AND endpoint.capability_key IN
                                  ('runtime-war-state', 'active-map-list', 'region-war-report')
                              AND attempt.outcome_code = 'captured_current'
                              AND normalization.id IS NULL
                        )
                        +
                        (
                            SELECT COUNT(*)
                            FROM evidence.coverage_observations AS coverage
                            INNER JOIN sources.endpoints AS endpoint
                                ON endpoint.id = coverage.endpoint_id
                            LEFT JOIN evidence.coverage_reprocessing_runs AS processed
                                ON processed.coverage_observation_id = coverage.id
                               AND processed.processor_version = 'warapi-coverage-reprocessor@1'
                            WHERE coverage.state = 'source_not_modified'
                              AND endpoint.capability_key = 'active-map-list'
                              AND endpoint.semantic_key = 'maps'
                              AND processed.id IS NULL
                        );
                    """);

            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public async Task<long> CountM5ProjectionViolationsAsync()
        {
            await using var command =
                dataSource.CreateCommand(
                    """
                    SELECT
                        (
                            SELECT COUNT(*)
                            FROM runtime.wars AS war
                            LEFT JOIN LATERAL (
                                SELECT
                                    MIN(observation.observed_at) AS first_observed_at,
                                    MAX(observation.observed_at) AS last_observed_at
                                FROM runtime.war_observations AS observation
                                WHERE observation.war_id = war.id
                            ) AS bounds ON TRUE
                            LEFT JOIN LATERAL (
                                SELECT observation.war_number
                                FROM runtime.war_observations AS observation
                                WHERE observation.war_id = war.id
                                ORDER BY
                                    observation.observed_at DESC,
                                    observation.representation_fetch_id DESC
                                LIMIT 1
                            ) AS latest ON TRUE
                            WHERE bounds.first_observed_at IS NULL
                               OR war.first_observed_at <> bounds.first_observed_at
                               OR war.last_observed_at <> bounds.last_observed_at
                               OR war.war_number IS DISTINCT FROM latest.war_number
                        )
                        +
                        (
                            SELECT COUNT(*)
                            FROM runtime.war_regions AS membership
                            INNER JOIN runtime.regions AS region
                                ON region.id = membership.region_id
                            WHERE membership.first_seen_at > membership.last_seen_at
                               OR region.canonical_key <>
                                  'official-war-api/map/' || membership.source_map_name
                               OR region.display_name <> membership.source_map_name
                        )
                        +
                        (
                            SELECT COUNT(*)
                            FROM runtime.war_report_observations AS report
                            INNER JOIN runtime.war_regions AS membership
                                ON membership.id = report.war_region_id
                            WHERE report.observed_at < membership.first_seen_at
                        );
                    """);

            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public async Task<long> CountCoverageStateAsync(
            string state)
        {
            await using var command =
                dataSource.CreateCommand(
                    """
                    SELECT COUNT(*)
                    FROM evidence.coverage_observations
                    WHERE state = @state;
                    """);
            command.Parameters.AddWithValue("state", state);

            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public async Task<long> CountNormalizationOutcomeAsync(
            string normalizerVersion,
            string outcome)
        {
            await using var command =
                dataSource.CreateCommand(
                    """
                    SELECT COUNT(*)
                    FROM evidence.normalization_runs
                    WHERE normalizer_version = @normalizer_version
                      AND outcome = @outcome;
                    """);
            command.Parameters.AddWithValue(
                "normalizer_version",
                normalizerVersion);
            command.Parameters.AddWithValue("outcome", outcome);

            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public async Task<long> CountNormalizationRunsAsync(
            string normalizerVersion)
        {
            await using var command =
                dataSource.CreateCommand(
                    """
                    SELECT COUNT(*)
                    FROM evidence.normalization_runs
                    WHERE normalizer_version = @normalizer_version;
                    """);
            command.Parameters.AddWithValue(
                "normalizer_version",
                normalizerVersion);

            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public async Task<long> CountAsync(string tableName)
        {
            await using var command =
                dataSource.CreateCommand(
                    $"SELECT COUNT(*) FROM {tableName};");
            return (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
        }

        public ValueTask DisposeAsync() =>
            dataSource.DisposeAsync();
    }
}
