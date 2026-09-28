using System.Text;
using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiMapTaxonomyTests
{
    [Fact]
    public void V1ProfileLoadsEmbeddedContractAndKeepsStableIdentity()
    {
        var profile = WarApiMapTaxonomyRegistry.Get(
            WarApiVersions.MapTaxonomy);

        Assert.Equal("warapi-map-taxonomy@1", profile.Version);
        Assert.Equal(WarApiCatalog.SourceKey, profile.Source);
        Assert.Equal(new DateOnly(2026, 9, 28), profile.ReviewedAt);
        Assert.Equal(3, profile.Teams.Count);
        Assert.Equal(61, profile.Icons.Count);
        Assert.Equal(5, profile.Flags.Count);

        Assert.Equal(
            profile.Icons.Count,
            profile.Icons.Select(x => x.RawIconType).Distinct().Count());
        Assert.Equal(
            profile.Icons.Count,
            profile.Icons.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            profile.Flags.Count,
            profile.Flags.Select(x => x.RawBit).Distinct().Count());
    }

    [Fact]
    public void DocumentedAndRemovedIconsHaveVersionedInterpretation()
    {
        var interpreter = CreateInterpreter();

        var salvage = interpreter.InterpretIcon(20);
        Assert.Equal(WarApiMapTaxonomyLookupStatus.Known, salvage.Status);
        Assert.Equal("icon.salvage-field", salvage.Entry!.Key);
        Assert.Equal("Salvage Field", salvage.Entry.Label);
        Assert.Equal(
            WarApiMapTaxonomyEntryStatus.Documented,
            salvage.Entry.Status);

        var staticBase = interpreter.InterpretIcon(5);
        Assert.Equal(WarApiMapTaxonomyLookupStatus.Known, staticBase.Status);
        Assert.Equal("icon.static-base-1", staticBase.Entry!.Key);
        Assert.Equal(
            WarApiMapTaxonomyEntryStatus.Removed,
            staticBase.Entry.Status);
        Assert.Equal(46, staticBase.Entry.RemovedUpdate);
    }

    [Fact]
    public void Icon97RemainsUnknownEvenThoughParserReportsUnknownCode()
    {
        var body = Encoding.UTF8.GetBytes(
            """
            {
              "regionId":1,
              "mapItems":[
                {
                  "teamId":"WARDENS",
                  "iconType":97,
                  "x":0.5,
                  "y":0.5,
                  "flags":0,
                  "viewDirection":0
                }
              ],
              "mapTextItems":[],
              "lastUpdated":1,
              "version":1
            }
            """);

        var parsed = new WarApiParser().Parse(
            WarApiCapabilities.DynamicMapState,
            body);

        Assert.Equal(
            WarApiParseOutcome.ParsedWithUnknowns,
            parsed.Outcome);
        Assert.Equal(1, parsed.UnknownCodeCount);

        var interpretation = CreateInterpreter().InterpretIcon(97);

        Assert.Equal(
            WarApiMapTaxonomyLookupStatus.Unknown,
            interpretation.Status);
        Assert.Equal(97, interpretation.RawIconType);
        Assert.Null(interpretation.Entry);
    }

    [Fact]
    public void TeamInterpretationIsExactAndOpen()
    {
        var interpreter = CreateInterpreter();

        var wardens = interpreter.InterpretTeam("WARDENS");
        Assert.Equal(WarApiMapTaxonomyLookupStatus.Known, wardens.Status);
        Assert.Equal("team.wardens", wardens.Entry!.Key);

        var unknown = interpreter.InterpretTeam("FUTURE_TEAM");
        Assert.Equal(WarApiMapTaxonomyLookupStatus.Unknown, unknown.Status);
        Assert.Equal("FUTURE_TEAM", unknown.RawValue);
        Assert.Null(unknown.Entry);

        var missing = interpreter.InterpretTeam(null);
        Assert.Equal(WarApiMapTaxonomyLookupStatus.Missing, missing.Status);
        Assert.Null(missing.RawValue);
    }

    [Fact]
    public void FlagsPreserveRawKnownAndUnknownBitPatterns()
    {
        var interpreter = CreateInterpreter();

        var interpretation = interpreter.InterpretFlags(
            0x01 | 0x04 | 0x20 | 0x40);

        Assert.Equal(
            WarApiMapFlagInterpretationStatus.ContainsUnknownBits,
            interpretation.Status);
        Assert.Equal(0x65, interpretation.RawFlags);
        Assert.Equal(0x25u, interpretation.KnownBits);
        Assert.Equal(0x40u, interpretation.UnknownBits);
        Assert.Equal(
            [
                "flag.is-victory-base",
                "flag.is-build-site",
                "flag.is-town-claimed",
            ],
            interpretation.KnownFlags.Select(x => x.Key).ToArray());

        var negative = interpreter.InterpretFlags(-1);
        Assert.Equal(
            WarApiMapFlagInterpretationStatus.ContainsUnknownBits,
            negative.Status);
        Assert.Equal(-1, negative.RawFlags);
        Assert.Equal(0x37u, negative.KnownBits);
        Assert.Equal(0xFFFFFFC8u, negative.UnknownBits);
    }

    [Fact]
    public void ZeroFlagsAreFullyKnownAndNullFlagsAreMissing()
    {
        var interpreter = CreateInterpreter();

        var zero = interpreter.InterpretFlags(0);
        Assert.Equal(
            WarApiMapFlagInterpretationStatus.FullyKnown,
            zero.Status);
        Assert.Equal(0u, zero.KnownBits);
        Assert.Equal(0u, zero.UnknownBits);
        Assert.Empty(zero.KnownFlags);

        var missing = interpreter.InterpretFlags(null);
        Assert.Equal(
            WarApiMapFlagInterpretationStatus.Missing,
            missing.Status);
        Assert.Null(missing.RawFlags);
    }

    [Fact]
    public void RemovedFlagMetadataDoesNotEraseInterpretation()
    {
        var interpretation = CreateInterpreter().InterpretFlags(0x02);

        var flag = Assert.Single(interpretation.KnownFlags);
        Assert.Equal("flag.is-home-base", flag.Key);
        Assert.Equal(
            WarApiMapTaxonomyEntryStatus.Removed,
            flag.Status);
        Assert.Equal(29, flag.RemovedUpdate);
        Assert.Equal(0u, interpretation.UnknownBits);
    }

    [Fact]
    public void UnknownTaxonomyVersionFailsClosed()
    {
        Assert.Throws<NotSupportedException>(
            () => WarApiMapTaxonomyRegistry.Get(
                "warapi-map-taxonomy@999"));
    }

    private static WarApiMapTaxonomyInterpreter CreateInterpreter() =>
        new(
            WarApiMapTaxonomyRegistry.Get(
                WarApiVersions.MapTaxonomy));
}
