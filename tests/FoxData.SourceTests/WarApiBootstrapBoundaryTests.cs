using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiBootstrapBoundaryTests
{
    [Fact]
    public void M3WarApiProjectNowOwnsHttpRequestConstruction()
    {
        var references = typeof(AssemblyMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.Contains("System.Net.Http", references);
    }

    [Fact]
    public void ProductionRootsRemainHttpsAndKnown()
    {
        foreach (var shard in Enum.GetValues<WarApiShard>())
        {
            var root = WarApiCatalog.GetRoot(shard);

            Assert.Equal(Uri.UriSchemeHttps, root.Scheme);
            Assert.EndsWith("/api/", root.AbsoluteUri, StringComparison.Ordinal);
        }
    }
}
