using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiCatalogTests
{
    [Theory]
    [InlineData(WarApiShard.Live1, "https://war-service-live.foxholeservices.com/api/")]
    [InlineData(WarApiShard.Live2, "https://war-service-live-2.foxholeservices.com/api/")]
    [InlineData(WarApiShard.Live3, "https://war-service-live-3.foxholeservices.com/api/")]
    [InlineData(WarApiShard.Dev, "https://war-service-dev.foxholeservices.com/api/")]
    public void RootsAreFixed(WarApiShard shard, string expected)
    {
        Assert.Equal(expected, WarApiCatalog.GetRoot(shard).AbsoluteUri);
    }

    [Fact]
    public void MapNameIsPreservedExactly()
    {
        var endpoint = WarApiCatalog.WarReport("MarbanHollow");

        Assert.Equal("MarbanHollow", endpoint.SourceIdentifier);
        Assert.Equal("war-report/MarbanHollow", endpoint.SemanticKey);
        Assert.Equal(
            "https://war-service-live.foxholeservices.com/api/worldconquest/warReport/MarbanHollow",
            WarApiCatalog.BuildUri(WarApiShard.Live1, endpoint).AbsoluteUri);
    }

    [Fact]
    public void MapNameCaseIsNotNormalized()
    {
        var endpoint = WarApiCatalog.DynamicMap("DeadLandsHex");

        Assert.Equal("DeadLandsHex", endpoint.SourceIdentifier);
        Assert.Contains("DeadLandsHex", WarApiCatalog.BuildUri(WarApiShard.Live1, endpoint).AbsoluteUri);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("line\nbreak")]
    [InlineData(" padded")]
    public void UnsafeMapNameIsRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => WarApiCatalog.StaticMap(value));
    }
}
