using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoxData.Sources.WarApi;

public enum WarApiMapTaxonomyEntryStatus
{
    Documented,
    Legacy,
    Removed,
}

public enum WarApiMapTaxonomyLookupStatus
{
    Missing,
    Known,
    Unknown,
}

public enum WarApiMapFlagInterpretationStatus
{
    Missing,
    FullyKnown,
    ContainsUnknownBits,
}

public sealed record WarApiMapTeamTaxonomyEntry(
    string RawValue,
    string Key,
    string Label,
    WarApiMapTaxonomyEntryStatus Status,
    string? Notes);

public sealed record WarApiMapIconTaxonomyEntry(
    int RawIconType,
    string Key,
    string Label,
    WarApiMapTaxonomyEntryStatus Status,
    int? IntroducedUpdate,
    int? RemovedUpdate,
    string? Notes);

public sealed record WarApiMapFlagTaxonomyEntry(
    uint RawBit,
    string Key,
    string Label,
    WarApiMapTaxonomyEntryStatus Status,
    int? IntroducedUpdate,
    int? RemovedUpdate,
    string? Notes);

public sealed record WarApiMapTaxonomyProfile(
    string Version,
    string Source,
    Uri DocumentationReference,
    DateOnly ReviewedAt,
    IReadOnlyList<WarApiMapTeamTaxonomyEntry> Teams,
    IReadOnlyList<WarApiMapIconTaxonomyEntry> Icons,
    IReadOnlyList<WarApiMapFlagTaxonomyEntry> Flags);

public sealed record WarApiMapTeamInterpretation(
    WarApiMapTaxonomyLookupStatus Status,
    string? RawValue,
    WarApiMapTeamTaxonomyEntry? Entry);

public sealed record WarApiMapIconInterpretation(
    WarApiMapTaxonomyLookupStatus Status,
    int? RawIconType,
    WarApiMapIconTaxonomyEntry? Entry);

public sealed record WarApiMapFlagsInterpretation(
    WarApiMapFlagInterpretationStatus Status,
    int? RawFlags,
    uint KnownBits,
    uint UnknownBits,
    IReadOnlyList<WarApiMapFlagTaxonomyEntry> KnownFlags);

public static class WarApiMapTaxonomyRegistry
{
    private const string V1ResourceName =
        "FoxData.Sources.WarApi.Contracts.warapi-map-taxonomy@1.json";

    private static readonly Lazy<WarApiMapTaxonomyProfile> V1 =
        new(
            () => LoadEmbedded(
                V1ResourceName,
                WarApiVersions.MapTaxonomy),
            LazyThreadSafetyMode.ExecutionAndPublication);

    public static WarApiMapTaxonomyProfile Get(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (!string.Equals(
                version,
                version.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Taxonomy version must already be trimmed.",
                nameof(version));
        }

        return string.Equals(
                version,
                WarApiVersions.MapTaxonomy,
                StringComparison.Ordinal)
            ? V1.Value
            : throw new NotSupportedException(
                $"Unsupported War API map taxonomy version '{version}'.");
    }

    private static WarApiMapTaxonomyProfile LoadEmbedded(
        string resourceName,
        string expectedVersion)
    {
        var assembly = typeof(WarApiMapTaxonomyRegistry).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded taxonomy contract '{resourceName}' was not found.");

        var document = JsonSerializer.Deserialize(
            stream,
            WarApiJsonContext.Default.WarApiMapTaxonomyDocumentDto)
            ?? throw new InvalidOperationException(
                $"Embedded taxonomy contract '{resourceName}' deserialized to null.");

        return BuildProfile(
            document,
            expectedVersion);
    }

    private static WarApiMapTaxonomyProfile BuildProfile(
        WarApiMapTaxonomyDocumentDto document,
        string expectedVersion)
    {
        ValidateRequiredText(
            document.Version,
            nameof(document.Version));
        ValidateRequiredText(
            document.Source,
            nameof(document.Source));
        ValidateRequiredText(
            document.DocumentationReference,
            nameof(document.DocumentationReference));
        ValidateRequiredText(
            document.ReviewedAt,
            nameof(document.ReviewedAt));

        if (!string.Equals(
                document.Version,
                expectedVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Taxonomy contract version '{document.Version}' does not match expected version '{expectedVersion}'.");
        }

        if (!string.Equals(
                document.Source,
                WarApiCatalog.SourceKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Taxonomy contract source '{document.Source}' does not match '{WarApiCatalog.SourceKey}'.");
        }

        if (!Uri.TryCreate(
                document.DocumentationReference,
                UriKind.Absolute,
                out var documentationReference) ||
            documentationReference.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "Taxonomy documentationReference must be an absolute HTTP(S) URI.");
        }

        if (!DateOnly.TryParseExact(
                document.ReviewedAt,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var reviewedAt))
        {
            throw new InvalidOperationException(
                "Taxonomy reviewedAt must use yyyy-MM-dd.");
        }

        var teams = BuildTeams(document.Teams);
        var icons = BuildIcons(document.Icons);
        var flags = BuildFlags(document.Flags);

        return new WarApiMapTaxonomyProfile(
            document.Version!,
            document.Source!,
            documentationReference,
            reviewedAt,
            new ReadOnlyCollection<WarApiMapTeamTaxonomyEntry>(teams),
            new ReadOnlyCollection<WarApiMapIconTaxonomyEntry>(icons),
            new ReadOnlyCollection<WarApiMapFlagTaxonomyEntry>(flags));
    }

    private static List<WarApiMapTeamTaxonomyEntry> BuildTeams(
        WarApiMapTeamTaxonomyEntryDto[]? source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var rawValues = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var results =
            new List<WarApiMapTeamTaxonomyEntry>(source.Length);

        foreach (var item in source)
        {
            ArgumentNullException.ThrowIfNull(item);
            ValidateRequiredText(item.RawValue, nameof(item.RawValue));
            ValidateTaxonomyKey(item.Key, "team.", nameof(item.Key));
            ValidateRequiredText(item.Label, nameof(item.Label));
            ValidateOptionalText(item.Notes, nameof(item.Notes));

            if (!rawValues.Add(item.RawValue!))
            {
                throw new InvalidOperationException(
                    $"Duplicate taxonomy team raw value '{item.RawValue}'.");
            }

            if (!keys.Add(item.Key!))
            {
                throw new InvalidOperationException(
                    $"Duplicate taxonomy team key '{item.Key}'.");
            }

            results.Add(
                new WarApiMapTeamTaxonomyEntry(
                    item.RawValue!,
                    item.Key!,
                    item.Label!,
                    ParseStatus(item.Status),
                    item.Notes));
        }

        return results;
    }

    private static List<WarApiMapIconTaxonomyEntry> BuildIcons(
        WarApiMapIconTaxonomyEntryDto[]? source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var rawValues = new HashSet<int>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var results =
            new List<WarApiMapIconTaxonomyEntry>(source.Length);

        foreach (var item in source)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (item.RawIconType is null or < 0)
            {
                throw new InvalidOperationException(
                    "Taxonomy icon rawIconType must be a non-negative integer.");
            }

            ValidateTaxonomyKey(item.Key, "icon.", nameof(item.Key));
            ValidateRequiredText(item.Label, nameof(item.Label));
            ValidateOptionalText(item.Notes, nameof(item.Notes));
            ValidateOptionalPositive(
                item.IntroducedUpdate,
                nameof(item.IntroducedUpdate));
            ValidateOptionalPositive(
                item.RemovedUpdate,
                nameof(item.RemovedUpdate));

            if (!rawValues.Add(item.RawIconType.Value))
            {
                throw new InvalidOperationException(
                    $"Duplicate taxonomy icon raw value '{item.RawIconType}'.");
            }

            if (!keys.Add(item.Key!))
            {
                throw new InvalidOperationException(
                    $"Duplicate taxonomy icon key '{item.Key}'.");
            }

            results.Add(
                new WarApiMapIconTaxonomyEntry(
                    item.RawIconType.Value,
                    item.Key!,
                    item.Label!,
                    ParseStatus(item.Status),
                    item.IntroducedUpdate,
                    item.RemovedUpdate,
                    item.Notes));
        }

        return results;
    }

    private static List<WarApiMapFlagTaxonomyEntry> BuildFlags(
        WarApiMapFlagTaxonomyEntryDto[]? source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var rawBits = new HashSet<uint>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var results =
            new List<WarApiMapFlagTaxonomyEntry>(source.Length);

        foreach (var item in source)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (item.RawBit is null or <= 0)
            {
                throw new InvalidOperationException(
                    "Taxonomy flag rawBit must be a positive integer.");
            }

            var rawBit = checked((uint)item.RawBit.Value);
            if ((rawBit & (rawBit - 1)) != 0)
            {
                throw new InvalidOperationException(
                    $"Taxonomy flag rawBit '{rawBit}' must contain exactly one bit.");
            }

            ValidateTaxonomyKey(item.Key, "flag.", nameof(item.Key));
            ValidateRequiredText(item.Label, nameof(item.Label));
            ValidateOptionalText(item.Notes, nameof(item.Notes));
            ValidateOptionalPositive(
                item.IntroducedUpdate,
                nameof(item.IntroducedUpdate));
            ValidateOptionalPositive(
                item.RemovedUpdate,
                nameof(item.RemovedUpdate));

            if (!rawBits.Add(rawBit))
            {
                throw new InvalidOperationException(
                    $"Duplicate taxonomy flag raw bit '{rawBit}'.");
            }

            if (!keys.Add(item.Key!))
            {
                throw new InvalidOperationException(
                    $"Duplicate taxonomy flag key '{item.Key}'.");
            }

            results.Add(
                new WarApiMapFlagTaxonomyEntry(
                    rawBit,
                    item.Key!,
                    item.Label!,
                    ParseStatus(item.Status),
                    item.IntroducedUpdate,
                    item.RemovedUpdate,
                    item.Notes));
        }

        return results;
    }

    private static WarApiMapTaxonomyEntryStatus ParseStatus(
        string? value) =>
        value switch
        {
            "documented" => WarApiMapTaxonomyEntryStatus.Documented,
            "legacy" => WarApiMapTaxonomyEntryStatus.Legacy,
            "removed" => WarApiMapTaxonomyEntryStatus.Removed,
            _ => throw new InvalidOperationException(
                $"Unknown taxonomy status '{value}'."),
        };

    private static void ValidateTaxonomyKey(
        string? value,
        string prefix,
        string parameterName)
    {
        ValidateRequiredText(value, parameterName);

        if (!value!.StartsWith(prefix, StringComparison.Ordinal) ||
            value.Length == prefix.Length)
        {
            throw new InvalidOperationException(
                $"Taxonomy key '{value}' must start with '{prefix}' and contain a stable suffix.");
        }

        foreach (var character in value[prefix.Length..])
        {
            if (!(character is >= 'a' and <= 'z' ||
                  character is >= '0' and <= '9' ||
                  character is '.' or '-'))
            {
                throw new InvalidOperationException(
                    $"Taxonomy key '{value}' contains an unsupported character.");
            }
        }
    }

    private static void ValidateRequiredText(
        string? value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(
                value,
                value.Trim(),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{parameterName} must be non-empty and already trimmed.");
        }
    }

    private static void ValidateOptionalText(
        string? value,
        string parameterName)
    {
        if (value is not null &&
            (string.IsNullOrWhiteSpace(value) ||
             !string.Equals(
                 value,
                 value.Trim(),
                 StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"{parameterName} must be null or non-empty and already trimmed.");
        }
    }

    private static void ValidateOptionalPositive(
        int? value,
        string parameterName)
    {
        if (value is <= 0)
        {
            throw new InvalidOperationException(
                $"{parameterName} must be positive when supplied.");
        }
    }
}

public sealed class WarApiMapTaxonomyInterpreter
{
    private readonly IReadOnlyDictionary<string, WarApiMapTeamTaxonomyEntry>
        _teams;
    private readonly IReadOnlyDictionary<int, WarApiMapIconTaxonomyEntry>
        _icons;
    private readonly IReadOnlyDictionary<uint, WarApiMapFlagTaxonomyEntry>
        _flags;
    private readonly uint _knownFlagMask;

    public WarApiMapTaxonomyInterpreter(
        WarApiMapTaxonomyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Profile = profile;

        _teams = new ReadOnlyDictionary<
            string,
            WarApiMapTeamTaxonomyEntry>(
            profile.Teams.ToDictionary(
                entry => entry.RawValue,
                StringComparer.Ordinal));
        _icons = new ReadOnlyDictionary<
            int,
            WarApiMapIconTaxonomyEntry>(
            profile.Icons.ToDictionary(
                entry => entry.RawIconType));
        _flags = new ReadOnlyDictionary<
            uint,
            WarApiMapFlagTaxonomyEntry>(
            profile.Flags.ToDictionary(
                entry => entry.RawBit));

        foreach (var entry in profile.Flags)
        {
            _knownFlagMask |= entry.RawBit;
        }
    }

    public WarApiMapTaxonomyProfile Profile { get; }

    public WarApiMapTeamInterpretation InterpretTeam(
        string? rawValue)
    {
        if (rawValue is null)
        {
            return new WarApiMapTeamInterpretation(
                WarApiMapTaxonomyLookupStatus.Missing,
                null,
                null);
        }

        return _teams.TryGetValue(rawValue, out var entry)
            ? new WarApiMapTeamInterpretation(
                WarApiMapTaxonomyLookupStatus.Known,
                rawValue,
                entry)
            : new WarApiMapTeamInterpretation(
                WarApiMapTaxonomyLookupStatus.Unknown,
                rawValue,
                null);
    }

    public WarApiMapIconInterpretation InterpretIcon(
        int? rawIconType)
    {
        if (rawIconType is null)
        {
            return new WarApiMapIconInterpretation(
                WarApiMapTaxonomyLookupStatus.Missing,
                null,
                null);
        }

        return _icons.TryGetValue(rawIconType.Value, out var entry)
            ? new WarApiMapIconInterpretation(
                WarApiMapTaxonomyLookupStatus.Known,
                rawIconType,
                entry)
            : new WarApiMapIconInterpretation(
                WarApiMapTaxonomyLookupStatus.Unknown,
                rawIconType,
                null);
    }

    public WarApiMapFlagsInterpretation InterpretFlags(
        int? rawFlags)
    {
        if (rawFlags is null)
        {
            return new WarApiMapFlagsInterpretation(
                WarApiMapFlagInterpretationStatus.Missing,
                null,
                0,
                0,
                Array.Empty<WarApiMapFlagTaxonomyEntry>());
        }

        var rawBits = unchecked((uint)rawFlags.Value);
        var knownBits = rawBits & _knownFlagMask;
        var unknownBits = rawBits & ~_knownFlagMask;
        var knownFlags = _flags
            .Where(pair => (knownBits & pair.Key) != 0)
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .ToArray();

        return new WarApiMapFlagsInterpretation(
            unknownBits == 0
                ? WarApiMapFlagInterpretationStatus.FullyKnown
                : WarApiMapFlagInterpretationStatus.ContainsUnknownBits,
            rawFlags,
            knownBits,
            unknownBits,
            knownFlags);
    }
}

internal sealed class WarApiMapTaxonomyDocumentDto
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("documentationReference")]
    public string? DocumentationReference { get; set; }

    [JsonPropertyName("reviewedAt")]
    public string? ReviewedAt { get; set; }

    [JsonPropertyName("teams")]
    public WarApiMapTeamTaxonomyEntryDto[]? Teams { get; set; }

    [JsonPropertyName("icons")]
    public WarApiMapIconTaxonomyEntryDto[]? Icons { get; set; }

    [JsonPropertyName("flags")]
    public WarApiMapFlagTaxonomyEntryDto[]? Flags { get; set; }
}

internal sealed class WarApiMapTeamTaxonomyEntryDto
{
    [JsonPropertyName("rawValue")]
    public string? RawValue { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

internal sealed class WarApiMapIconTaxonomyEntryDto
{
    [JsonPropertyName("rawIconType")]
    public int? RawIconType { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("introducedUpdate")]
    public int? IntroducedUpdate { get; set; }

    [JsonPropertyName("removedUpdate")]
    public int? RemovedUpdate { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

internal sealed class WarApiMapFlagTaxonomyEntryDto
{
    [JsonPropertyName("rawBit")]
    public int? RawBit { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("introducedUpdate")]
    public int? IntroducedUpdate { get; set; }

    [JsonPropertyName("removedUpdate")]
    public int? RemovedUpdate { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}
