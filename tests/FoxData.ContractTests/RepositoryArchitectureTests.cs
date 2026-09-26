using System.Text.Json;
using System.Xml.Linq;

namespace FoxData.ContractTests;

public sealed class RepositoryArchitectureTests
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedReferences =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["FoxData.Core"] = [],
            ["FoxData.Application"] = ["FoxData.Core"],
            ["FoxData.Infrastructure"] = ["FoxData.Core", "FoxData.Application"],
            ["FoxData.Hosting"] = [],
            ["FoxData.Sources.Abstractions"] = [],
            ["FoxData.Sources.WarApi"] = ["FoxData.Sources.Abstractions"],
            ["FoxData.Api"] = ["FoxData.Application", "FoxData.Infrastructure", "FoxData.Hosting"],
            ["FoxData.Worker"] =
                ["FoxData.Application", "FoxData.Infrastructure", "FoxData.Hosting", "FoxData.Sources.WarApi"],
            ["FoxData.Cli"] = ["FoxData.Application", "FoxData.Infrastructure", "FoxData.Hosting"],
        };

    [Fact]
    public void ProductionProjectReferencesMatchTheAllowedGraph()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");

        foreach (var projectPath in Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            Assert.True(AllowedReferences.TryGetValue(projectName, out var allowed), $"Unknown production project: {projectName}");

            var document = XDocument.Load(projectPath);
            var references = document
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => Path.GetFileNameWithoutExtension(value))
                .ToArray();

            foreach (var reference in references)
            {
                Assert.Contains(reference, allowed!);
            }
        }
    }

    [Fact]
    public void CanonicalContractsAndGovernanceArePresent()
    {
        var root = FindRepositoryRoot();

        Assert.True(File.Exists(Path.Combine(root, "AGENTS.md")));
        Assert.True(File.Exists(Path.Combine(root, ".work", "M2_EVIDENCE_KERNEL.md")));
        Assert.True(File.Exists(Path.Combine(root, ".work", "M4_SOURCE_MEASUREMENT.md")));
        Assert.True(File.Exists(Path.Combine(root, ".work", "M4_RUNBOOK.md")));
        Assert.True(File.Exists(Path.Combine(root, ".work", "M5_CANONICAL_WAR_REGION_REPORT.md")));
        Assert.True(
            File.Exists(
                Path.Combine(
                    root,
                    ".github",
                    "workflows",
                    "m4-live-campaign.yml")));
        Assert.True(
            File.Exists(
                Path.Combine(
                    root,
                    ".github",
                    "actions",
                    "m4-segment",
                    "action.yml")));
        Assert.True(File.Exists(Path.Combine(root, ".work", "contracts", "openapi", "public-v1.yaml")));
        Assert.True(File.Exists(Path.Combine(root, ".work", "contracts", "openapi", "compat-warapi-v1.yaml")));
        Assert.True(File.Exists(Path.Combine(root, ".work", "contracts", "asyncapi", "events-v1.yaml")));
        Assert.True(File.Exists(Path.Combine(root, ".work", "contracts", "jsonrpc", "stdio-v1.md")));
        Assert.True(
            File.Exists(
                Path.Combine(
                    root,
                    ".work",
                    "contracts",
                    "internal",
                    "source",
                    "collection-profile.schema.json")));
    }

    [Fact]
    public void MeasuredCollectionProfileContractRequiresProvenance()
    {
        var root = FindRepositoryRoot();
        var schemaPath = Path.Combine(
            root,
            ".work",
            "contracts",
            "internal",
            "source",
            "collection-profile.schema.json");

        using var document = JsonDocument.Parse(
            File.ReadAllText(schemaPath));

        var required = document.RootElement
            .GetProperty("required")
            .EnumerateArray()
            .Select(element => element.GetString())
            .Where(value => value is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("measurementReference", required);
        Assert.Contains("limitations", required);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FoxData.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Foxhole-Data repository root.");
    }
}
