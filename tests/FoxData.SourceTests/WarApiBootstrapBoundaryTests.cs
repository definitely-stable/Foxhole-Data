namespace FoxData.SourceTests;

public sealed class WarApiBootstrapBoundaryTests
{
    [Fact]
    public void M1WarApiProjectDoesNotReferenceHttpClient()
    {
        var references = typeof(FoxData.Sources.WarApi.AssemblyMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.DoesNotContain("System.Net.Http", references);
    }
}
