using System.Text;
using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiParserTests
{
    private readonly WarApiParser _parser = new();

    [Fact]
    public void DynamicMapPreservesIcon97AndViewDirection()
    {
        var result = Parse(
            WarApiCapabilities.DynamicMapState,
            """
            {
              "regionId": 1,
              "scorchedVictoryTowns": 0,
              "mapItems": [{
                "teamId": "COLONIALS",
                "iconType": 97,
                "x": 0.71438116,
                "y": 0.5286482,
                "flags": 0,
                "viewDirection": 0
              }],
              "mapTextItems": [],
              "lastUpdated": 1,
              "version": 2
            }
            """);

        Assert.True(result.Parsed);
        Assert.Equal(WarApiParseOutcome.ParsedWithUnknowns, result.Outcome);

        var map = Assert.IsType<WarApiMapDataDto>(result.Value);
        var item = Assert.Single(map.MapItems!);

        Assert.Equal(97, item.IconType);
        Assert.Equal(0, item.ViewDirection);
        Assert.Equal("COLONIALS", item.TeamId);
        Assert.Equal(1, result.UnknownCodeCount);
    }

    [Fact]
    public void AdditiveFieldsArePreserved()
    {
        var result = Parse(
            WarApiCapabilities.RuntimeWarState,
            """
            {
              "warId":"abc",
              "warNumber":129,
              "winner":"NONE",
              "conquestStartTime":null,
              "futureField":{"enabled":true}
            }
            """);

        Assert.Equal(WarApiParseOutcome.ParsedWithUnknowns, result.Outcome);

        var war = Assert.IsType<WarApiWarStateDto>(result.Value);
        Assert.True(war.ExtensionData!.ContainsKey("futureField"));
        Assert.Equal(1, result.UnknownPropertyCount);
    }

    [Fact]
    public void NullPreConquestTimesAreAccepted()
    {
        var result = Parse(
            WarApiCapabilities.RuntimeWarState,
            """
            {
              "warId":"abc",
              "warNumber":129,
              "winner":"NONE",
              "conquestStartTime":null,
              "conquestEndTime":null,
              "resistanceStartTime":null,
              "scheduledConquestEndTime":null,
              "requiredVictoryTowns":20,
              "shortRequiredVictoryTowns":10
            }
            """);

        Assert.Equal(WarApiParseOutcome.Parsed, result.Outcome);
        var war = Assert.IsType<WarApiWarStateDto>(result.Value);
        Assert.Null(war.ConquestStartTime);
    }

    [Fact]
    public void MapListRetainsOpaqueSourceNames()
    {
        var result = Parse(
            WarApiCapabilities.ActiveMapList,
            """["MarbanHollow","DeadLandsHex","RedRiverHex"]""");

        var maps = Assert.IsType<string[]>(result.Value);
        Assert.Equal(
            new[] { "MarbanHollow", "DeadLandsHex", "RedRiverHex" },
            maps);
    }

    [Fact]
    public void UnknownTeamDoesNotFailParsing()
    {
        var result = Parse(
            WarApiCapabilities.DynamicMapState,
            """
            {
              "mapItems": [{
                "teamId": "FUTURE_FACTION",
                "iconType": 5,
                "x": 0.1,
                "y": 0.2,
                "flags": 0
              }]
            }
            """);

        Assert.Equal(WarApiParseOutcome.ParsedWithUnknowns, result.Outcome);
        Assert.Equal(1, result.UnknownCodeCount);

        var map = Assert.IsType<WarApiMapDataDto>(result.Value);
        Assert.Equal("FUTURE_FACTION", Assert.Single(map.MapItems!).TeamId);
    }

    [Theory]
    [InlineData("runtime-war-state", "{\"warId\":\"abc\",\"warNumber\":\"129\"}")]
    [InlineData("region-war-report", "{\"totalEnlistments\":\"many\"}")]
    [InlineData("static-map-state", "{\"version\":\"new\"}")]
    [InlineData("dynamic-map-state", "{\"mapItems\":{}}")]
    public void ValidJsonWithIncompatibleKnownShapeIsClassifiedSeparately(
        string capabilityKey,
        string json)
    {
        var capability = capabilityKey switch
        {
            "runtime-war-state" => WarApiCapabilities.RuntimeWarState,
            "region-war-report" => WarApiCapabilities.RegionWarReport,
            "static-map-state" => WarApiCapabilities.StaticMapState,
            "dynamic-map-state" => WarApiCapabilities.DynamicMapState,
            _ => throw new ArgumentOutOfRangeException(nameof(capabilityKey)),
        };

        var result = Parse(capability, json);

        Assert.Equal(WarApiParseOutcome.IncompatibleShape, result.Outcome);
        Assert.Null(result.Value);
        Assert.Equal("incompatible_shape", result.ErrorCode);
        Assert.NotNull(result.StructuralFingerprint);
    }

    [Fact]
    public void WarReportParsesKnownShape()
    {
        var result = Parse(
            WarApiCapabilities.RegionWarReport,
            """
            {
              "totalEnlistments":1234,
              "colonialCasualties":100,
              "wardenCasualties":120,
              "dayOfWar":3
            }
            """);

        Assert.Equal(WarApiParseOutcome.Parsed, result.Outcome);
        var report = Assert.IsType<WarApiWarReportDto>(result.Value);
        Assert.Equal(1234, report.TotalEnlistments);
        Assert.Equal(3, report.DayOfWar);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaticAndDynamicMapFamiliesShareTolerantSourceShape(bool dynamic)
    {
        var capability = dynamic
            ? WarApiCapabilities.DynamicMapState
            : WarApiCapabilities.StaticMapState;

        var result = Parse(
            capability,
            """
            {
              "regionId":1,
              "mapItems":[],
              "mapTextItems":[],
              "lastUpdated":10,
              "version":2
            }
            """);

        Assert.Equal(WarApiParseOutcome.Parsed, result.Outcome);
        Assert.IsType<WarApiMapDataDto>(result.Value);
    }

    [Fact]
    public void MalformedJsonIsClassifiedWithoutThrowing()
    {
        var result = Parse(
            WarApiCapabilities.RuntimeWarState,
            """{"warId":""");

        Assert.Equal(WarApiParseOutcome.MalformedJson, result.Outcome);
        Assert.Null(result.Value);
        Assert.Equal("malformed_json", result.ErrorCode);
    }

    private WarApiParseResult Parse(
        FoxData.Sources.Abstractions.SourceCapability capability,
        string json) =>
        _parser.Parse(capability, Encoding.UTF8.GetBytes(json));
}
