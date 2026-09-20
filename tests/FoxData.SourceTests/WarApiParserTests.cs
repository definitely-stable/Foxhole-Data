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
