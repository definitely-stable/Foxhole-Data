using System.Text.Json;
using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiMapQualityFeatureTests
{
    private readonly WarApiParser _parser = new();
    private readonly WarApiMapTaxonomyInterpreter _taxonomy =
        new(
            WarApiMapTaxonomyRegistry.Get(
                WarApiVersions.MapTaxonomy));

    [Fact]
    public void HistoricalIssue92PayloadProducesDeterministicCollapseFeatures()
    {
        var baseline = Load("healthy-dynamic-baseline.json");
        var incident = Load("warapi-92-restart-mass-none.json");

        var features = WarApiMapQualityFeatureExtractor.Extract(
            incident,
            baseline,
            _taxonomy);

        Assert.True(features.HasBaseline);
        Assert.Equal(18, features.ItemCount);
        Assert.Equal(18, features.NoneCount);
        Assert.Equal(1d, features.NoneShare);
        Assert.Equal(0d, features.OwnedTeamShare);
        Assert.Equal(0.75d, features.ItemCountRatio);
        Assert.Equal(-7m, features.SourceVersionDelta);
        Assert.True(features.NoneShareDelta > 0.9d);
        Assert.True(features.OwnedTeamShareDelta < -0.8d);
        Assert.Equal(8, features.DistinctIconTypeCount);
        Assert.Equal(0, features.UnknownTeamCount);
    }

    [Fact]
    public void NoBaselineNeutralFixtureDoesNotInventDeltaFeatures()
    {
        var current = Load("war-start-neutral.json");

        var features = WarApiMapQualityFeatureExtractor.Extract(
            current,
            null,
            _taxonomy);

        Assert.False(features.HasBaseline);
        Assert.Equal(1d, features.NoneShare);
        Assert.Null(features.NoneShareDelta);
        Assert.Null(features.ItemCountRatio);
        Assert.Null(features.SourceVersionDelta);
        Assert.Null(features.RegionIdChanged);
    }

    [Fact]
    public void OpenTaxonomyAndDuplicateFixturesRemainObservable()
    {
        var icon97 = WarApiMapQualityFeatureExtractor.Extract(
            Load("warapi-137-icon97-viewdirection.json"),
            null,
            _taxonomy);
        Assert.Equal(1, icon97.UnknownIconCount);

        var duplicate = WarApiMapQualityFeatureExtractor.Extract(
            Load("warapi-115-duplicate-rocket-target.json"),
            null,
            _taxonomy);
        Assert.Equal(1, duplicate.DuplicateItemGroupCount);
        Assert.Equal(1, duplicate.DuplicateItemExcessCount);

        var unknownTeam = WarApiMapQualityFeatureExtractor.Extract(
            Load("unknown-team.json"),
            null,
            _taxonomy);
        Assert.Equal(1, unknownTeam.UnknownTeamCount);

        var unknownFlags = WarApiMapQualityFeatureExtractor.Extract(
            Load("unknown-flag-bits.json"),
            null,
            _taxonomy);
        Assert.Equal(1, unknownFlags.UnknownFlagOccurrenceCount);
        Assert.True(unknownFlags.UnknownFlagBitCount > 0);
    }

    [Fact]
    public void ProgressionAndStructuralFixturesExposeFactsWithoutPolicyThresholds()
    {
        var baseline = Load("healthy-dynamic-baseline.json");

        var version = WarApiMapQualityFeatureExtractor.Extract(
            Load("source-version-regression.json"),
            baseline,
            _taxonomy);
        Assert.Equal(-4m, version.SourceVersionDelta);

        var updated = WarApiMapQualityFeatureExtractor.Extract(
            Load("last-updated-regression.json"),
            baseline,
            _taxonomy);
        Assert.Equal(
            -100000m,
            updated.SourceLastUpdatedDeltaMilliseconds);

        var conflict = WarApiMapQualityFeatureExtractor.Extract(
            Load("source-region-conflict.json"),
            baseline,
            _taxonomy);
        Assert.True(conflict.RegionIdChanged);

        var coordinates = WarApiMapQualityFeatureExtractor.Extract(
            Load("invalid-coordinate.json"),
            null,
            _taxonomy);
        Assert.Equal(1, coordinates.InvalidCoordinateCount);
    }

    [Fact]
    public void FixtureCatalogMarksIncompleteUpstreamCasesAsSyntheticMinimized()
    {
        using var catalog = JsonDocument.Parse(
            File.ReadAllText(FixturePath("catalog.json")));
        var fixtures = catalog.RootElement
            .GetProperty("fixtures")
            .EnumerateArray()
            .ToArray();

        Assert.True(fixtures.Length >= 14);

        var issue92 = Assert.Single(
            fixtures,
            fixture => fixture.GetProperty("id").GetString() ==
                "warapi-92-restart-mass-none");
        Assert.Equal(
            "upstream-exact",
            issue92.GetProperty("provenanceKind").GetString());

        foreach (var id in new[]
                 {
                     "warapi-115-duplicate-rocket-target",
                     "warapi-137-icon97-viewdirection",
                     "warapi-77-static-before",
                     "warapi-77-static-changed",
                 })
        {
            var fixture = Assert.Single(
                fixtures,
                item => item.GetProperty("id").GetString() == id);
            Assert.Equal(
                "synthetic-minimized",
                fixture.GetProperty("provenanceKind").GetString());
        }
    }

    private WarApiMapQualityFeatureSnapshot Load(string file)
    {
        var capability = file.StartsWith(
            "warapi-77-static-",
            StringComparison.Ordinal)
            ? WarApiCapabilities.StaticMapState
            : WarApiCapabilities.DynamicMapState;
        var parsed = _parser.Parse(
            capability,
            File.ReadAllBytes(FixturePath(file)));

        Assert.True(parsed.Parsed);
        return WarApiMapQualityFeatureSnapshot.FromDto(
            Assert.IsType<WarApiMapDataDto>(parsed.Value));
    }

    private static string FixturePath(string file) =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "M6",
            file);
}
