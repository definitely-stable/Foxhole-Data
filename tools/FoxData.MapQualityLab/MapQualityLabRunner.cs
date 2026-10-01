using System.Text.Json;
using FoxData.Sources.WarApi;

namespace FoxData.MapQualityLab;

internal static class MapQualityLabRunner
{
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 1 &&
            args[0] is "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        if (args.Length == 1 &&
            args[0] == "--version")
        {
            Console.WriteLine(
                typeof(MapQualityLabRunner).Assembly
                    .GetName().Version?.ToString()
                ?? "0.0.0");
            return 0;
        }

        try
        {
            var options = Parse(args);
            var catalogPath = Path.GetFullPath(options.CatalogPath);
            var root = Path.GetDirectoryName(catalogPath)
                ?? throw new InvalidOperationException(
                    "Catalog path has no parent directory.");
            var catalog = JsonSerializer.Deserialize<FixtureCatalog>(
                File.ReadAllText(catalogPath),
                SerializerOptions)
                ?? throw new InvalidOperationException(
                    "Fixture catalog deserialized to null.");

            ValidateCatalog(catalog);
            var byId = catalog.Fixtures.ToDictionary(
                fixture => fixture.Id,
                StringComparer.Ordinal);
            var parser = new WarApiParser();
            var taxonomy = new WarApiMapTaxonomyInterpreter(
                WarApiMapTaxonomyRegistry.Get(
                    WarApiVersions.MapTaxonomy));
            var output = new List<string>();

            foreach (var fixture in catalog.Fixtures
                         .OrderBy(
                             fixture => fixture.Id,
                             StringComparer.Ordinal))
            {
                var current = Load(
                    parser,
                    root,
                    fixture);
                WarApiMapQualityFeatureSnapshot? baseline = null;
                if (fixture.BaselineFixtureId is { } baselineId)
                {
                    baseline = Load(
                        parser,
                        root,
                        byId[baselineId]);
                }

                var features =
                    WarApiMapQualityFeatureExtractor.Extract(
                        current,
                        baseline,
                        taxonomy);
                output.Add(
                    JsonSerializer.Serialize(
                        new LabRecord(
                            catalog.Version,
                            fixture.Id,
                            fixture.CapabilityKey,
                            fixture.Role,
                            fixture.ProvenanceKind,
                            fixture.BaselineFixtureId,
                            features),
                        SerializerOptions));
            }

            if (options.OutputPath is null)
            {
                foreach (var line in output)
                {
                    Console.WriteLine(line);
                }
            }
            else
            {
                var destination =
                    Path.GetFullPath(options.OutputPath);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(destination)
                    ?? Directory.GetCurrentDirectory());
                File.WriteAllLines(destination, output);
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static WarApiMapQualityFeatureSnapshot Load(
        WarApiParser parser,
        string root,
        FixtureEntry fixture)
    {
        var capability = fixture.CapabilityKey switch
        {
            "dynamic-map-state" =>
                WarApiCapabilities.DynamicMapState,
            "static-map-state" =>
                WarApiCapabilities.StaticMapState,
            _ => throw new InvalidOperationException(
                $"Fixture '{fixture.Id}' has unsupported capability '{fixture.CapabilityKey}'."),
        };

        var path = Path.Combine(root, fixture.File);
        var parsed = parser.Parse(
            capability,
            File.ReadAllBytes(path));
        if (!parsed.Parsed ||
            parsed.Value is not WarApiMapDataDto map)
        {
            throw new InvalidOperationException(
                $"Fixture '{fixture.Id}' does not parse as map data: {parsed.Outcome}/{parsed.ErrorCode}.");
        }

        return WarApiMapQualityFeatureSnapshot.FromDto(map);
    }

    private static Options Parse(string[] args)
    {
        var catalog =
            ".work/fixtures/m6-map-quality/catalog.json";
        string? output = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--catalog":
                    catalog = RequiredValue(args, ref index);
                    break;
                case "--output":
                    output = RequiredValue(args, ref index);
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown argument '{args[index]}'. Use --help.");
            }
        }

        return new Options(catalog, output);
    }

    private static string RequiredValue(
        string[] args,
        ref int index)
    {
        if (++index >= args.Length ||
            string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException(
                "Command option requires a non-empty value.");
        }

        return args[index];
    }

    private static void ValidateCatalog(FixtureCatalog catalog)
    {
        if (!string.Equals(
                catalog.Version,
                "m6-map-quality-fixtures@1",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported fixture catalog version '{catalog.Version}'.");
        }

        if (catalog.Fixtures.Count == 0)
        {
            throw new InvalidOperationException(
                "Fixture catalog must not be empty.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fixture in catalog.Fixtures)
        {
            if (string.IsNullOrWhiteSpace(fixture.Id) ||
                string.IsNullOrWhiteSpace(fixture.File) ||
                string.IsNullOrWhiteSpace(fixture.CapabilityKey) ||
                string.IsNullOrWhiteSpace(fixture.Role) ||
                string.IsNullOrWhiteSpace(fixture.ProvenanceKind))
            {
                throw new InvalidOperationException(
                    "Fixture metadata contains an empty required field.");
            }

            if (!ids.Add(fixture.Id))
            {
                throw new InvalidOperationException(
                    $"Duplicate fixture id '{fixture.Id}'.");
            }
        }

        foreach (var fixture in catalog.Fixtures)
        {
            if (fixture.BaselineFixtureId is { } baseline &&
                !ids.Contains(baseline))
            {
                throw new InvalidOperationException(
                    $"Fixture '{fixture.Id}' references missing baseline '{baseline}'.");
            }
        }
    }

    private static void PrintUsage() =>
        Console.WriteLine(
            "Usage: foxdata-map-quality-lab [--catalog <catalog.json>] [--output <features.jsonl>]");

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
        };

    private sealed record Options(
        string CatalogPath,
        string? OutputPath);

    private sealed record FixtureCatalog(
        string Version,
        string ReviewedAt,
        IReadOnlyList<FixtureEntry> Fixtures);

    private sealed record FixtureEntry(
        string Id,
        string File,
        string CapabilityKey,
        string SourceMapName,
        string Role,
        string ProvenanceKind,
        string? SourceUrl,
        string? BaselineFixtureId,
        string Notes);

    private sealed record LabRecord(
        string CatalogVersion,
        string FixtureId,
        string CapabilityKey,
        string Role,
        string ProvenanceKind,
        string? BaselineFixtureId,
        WarApiMapQualityFeatureVector Features);
}
