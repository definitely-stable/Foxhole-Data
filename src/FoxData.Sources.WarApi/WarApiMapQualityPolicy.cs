using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoxData.Sources.WarApi;

public enum WarApiMapQualityEffect
{
    Informational,
    Suspect,
    Quarantined,
}

public enum WarApiMapQualityPolicyDecision
{
    Accepted,
    Suspect,
    Quarantined,
}

public sealed record WarApiMapQualityAggregationProfile(
    WarApiMapQualityPolicyDecision NoFindingsDecision,
    WarApiMapQualityPolicyDecision InformationalDecision,
    WarApiMapQualityPolicyDecision SuspectDecision,
    WarApiMapQualityPolicyDecision QuarantinedDecision);

public sealed record WarApiMapQualityRuleProfile(
    string Key,
    string RuleVersion,
    string ConfigurationVersion,
    WarApiMapQualityEffect Effect,
    IReadOnlyDictionary<string, JsonElement> Parameters);

public sealed record WarApiMapQualityPolicyProfile(
    string Version,
    string Source,
    string TaxonomyVersion,
    DateOnly ReviewedAt,
    WarApiMapQualityAggregationProfile Aggregation,
    IReadOnlyList<WarApiMapQualityRuleProfile> Rules);

public static class WarApiMapQualityPolicyRegistry
{
    private const string V1ResourceName =
        "FoxData.Sources.WarApi.Contracts.warapi-map-quality-policy@1.json";

    private static readonly string[] ExpectedV1RuleKeys =
    [
        "region-id.valid",
        "region-id.conflict",
        "coordinate.valid",
        "source-time.representable",
        "schema.structure-changed",
        "taxonomy.unknown-icon",
        "taxonomy.unknown-team",
        "taxonomy.unknown-flag-bits",
    ];

    private static readonly Lazy<WarApiMapQualityPolicyProfile> V1 =
        new(
            () => LoadEmbedded(
                V1ResourceName,
                WarApiVersions.MapQualityPolicy),
            LazyThreadSafetyMode.ExecutionAndPublication);

    public static WarApiMapQualityPolicyProfile Get(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (!string.Equals(
                version,
                version.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Quality policy version must already be trimmed.",
                nameof(version));
        }

        return string.Equals(
                version,
                WarApiVersions.MapQualityPolicy,
                StringComparison.Ordinal)
            ? V1.Value
            : throw new NotSupportedException(
                $"Unsupported War API map quality policy version '{version}'.");
    }

    private static WarApiMapQualityPolicyProfile LoadEmbedded(
        string resourceName,
        string expectedVersion)
    {
        var assembly = typeof(WarApiMapQualityPolicyRegistry).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded quality policy contract '{resourceName}' was not found.");

        var document = JsonSerializer.Deserialize(
            stream,
            WarApiJsonContext.Default.WarApiMapQualityPolicyDocumentDto)
            ?? throw new InvalidOperationException(
                $"Embedded quality policy contract '{resourceName}' deserialized to null.");

        return BuildProfile(
            document,
            expectedVersion);
    }

    private static WarApiMapQualityPolicyProfile BuildProfile(
        WarApiMapQualityPolicyDocumentDto document,
        string expectedVersion)
    {
        ValidateRequiredText(
            document.Version,
            nameof(document.Version));
        ValidateRequiredText(
            document.Source,
            nameof(document.Source));
        ValidateRequiredText(
            document.TaxonomyVersion,
            nameof(document.TaxonomyVersion));
        ValidateRequiredText(
            document.ReviewedAt,
            nameof(document.ReviewedAt));

        if (!string.Equals(
                document.Version,
                expectedVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Quality policy contract version '{document.Version}' does not match expected version '{expectedVersion}'.");
        }

        if (!string.Equals(
                document.Source,
                WarApiCatalog.SourceKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Quality policy contract source '{document.Source}' does not match '{WarApiCatalog.SourceKey}'.");
        }

        if (!string.Equals(
                document.TaxonomyVersion,
                WarApiVersions.MapTaxonomy,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Quality policy taxonomy '{document.TaxonomyVersion}' does not match required taxonomy '{WarApiVersions.MapTaxonomy}'.");
        }

        _ = WarApiMapTaxonomyRegistry.Get(document.TaxonomyVersion!);

        if (!DateOnly.TryParseExact(
                document.ReviewedAt,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var reviewedAt))
        {
            throw new InvalidOperationException(
                "Quality policy reviewedAt must use yyyy-MM-dd.");
        }

        var aggregation = BuildAggregation(document.Aggregation);
        var rules = BuildRules(document.Rules);

        return new WarApiMapQualityPolicyProfile(
            document.Version!,
            document.Source!,
            document.TaxonomyVersion!,
            reviewedAt,
            aggregation,
            new ReadOnlyCollection<WarApiMapQualityRuleProfile>(rules));
    }

    private static WarApiMapQualityAggregationProfile BuildAggregation(
        WarApiMapQualityAggregationDto? source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var noFindings = ParseDecision(
            source.NoFindingsDecision,
            nameof(source.NoFindingsDecision));
        var informational = ParseDecision(
            source.InformationalDecision,
            nameof(source.InformationalDecision));
        var suspect = ParseDecision(
            source.SuspectDecision,
            nameof(source.SuspectDecision));
        var quarantined = ParseDecision(
            source.QuarantinedDecision,
            nameof(source.QuarantinedDecision));

        if (noFindings != WarApiMapQualityPolicyDecision.Accepted ||
            informational != WarApiMapQualityPolicyDecision.Accepted ||
            suspect != WarApiMapQualityPolicyDecision.Suspect ||
            quarantined != WarApiMapQualityPolicyDecision.Quarantined)
        {
            throw new InvalidOperationException(
                "Quality policy aggregation does not match the v1 decision contract.");
        }

        return new WarApiMapQualityAggregationProfile(
            noFindings,
            informational,
            suspect,
            quarantined);
    }

    private static List<WarApiMapQualityRuleProfile> BuildRules(
        WarApiMapQualityRuleDto[]? source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Length != ExpectedV1RuleKeys.Length)
        {
            throw new InvalidOperationException(
                $"Quality policy v1 must define exactly {ExpectedV1RuleKeys.Length} structural rules.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var ruleVersions = new HashSet<string>(StringComparer.Ordinal);
        var configurationVersions =
            new HashSet<string>(StringComparer.Ordinal);
        var results =
            new List<WarApiMapQualityRuleProfile>(source.Length);

        foreach (var item in source)
        {
            ArgumentNullException.ThrowIfNull(item);

            ValidateRuleKey(item.Key);
            ValidateRequiredText(
                item.RuleVersion,
                nameof(item.RuleVersion));
            ValidateRequiredText(
                item.ConfigurationVersion,
                nameof(item.ConfigurationVersion));

            if (!keys.Add(item.Key!))
            {
                throw new InvalidOperationException(
                    $"Duplicate quality rule key '{item.Key}'.");
            }

            if (!ruleVersions.Add(item.RuleVersion!))
            {
                throw new InvalidOperationException(
                    $"Duplicate quality rule version '{item.RuleVersion}'.");
            }

            if (!configurationVersions.Add(item.ConfigurationVersion!))
            {
                throw new InvalidOperationException(
                    $"Duplicate quality configuration version '{item.ConfigurationVersion}'.");
            }

            var expectedRuleVersion = $"{item.Key}@1";
            if (!string.Equals(
                    item.RuleVersion,
                    expectedRuleVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Quality rule '{item.Key}' must use v1 rule version '{expectedRuleVersion}'.");
            }

            var expectedConfigurationVersion =
                $"{item.Key}-config@1";
            if (!string.Equals(
                    item.ConfigurationVersion,
                    expectedConfigurationVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Quality rule '{item.Key}' must use v1 configuration version '{expectedConfigurationVersion}'.");
            }

            var effect = ParseEffect(
                item.Effect,
                nameof(item.Effect));
            ValidateV1Rule(
                item.Key!,
                effect,
                item.Parameters);

            results.Add(
                new WarApiMapQualityRuleProfile(
                    item.Key!,
                    item.RuleVersion!,
                    item.ConfigurationVersion!,
                    effect,
                    CopyParameters(item.Parameters)));
        }

        if (!keys.SetEquals(ExpectedV1RuleKeys))
        {
            throw new InvalidOperationException(
                "Quality policy v1 rule set does not match the expected structural rule contract.");
        }

        return results;
    }

    private static void ValidateV1Rule(
        string key,
        WarApiMapQualityEffect effect,
        JsonElement parameters)
    {
        switch (key)
        {
            case "region-id.valid":
                RequireEffect(
                    key,
                    effect,
                    WarApiMapQualityEffect.Quarantined);
                ValidateObjectProperties(
                    key,
                    parameters,
                    "allowNull",
                    "minimum");
                RequireBoolean(
                    key,
                    parameters,
                    "allowNull",
                    expected: true);
                RequireInteger(
                    key,
                    parameters,
                    "minimum",
                    expected: 0);
                break;

            case "region-id.conflict":
                RequireEffect(
                    key,
                    effect,
                    WarApiMapQualityEffect.Quarantined);
                ValidateObjectProperties(
                    key,
                    parameters,
                    "blockOnConflict");
                RequireBoolean(
                    key,
                    parameters,
                    "blockOnConflict",
                    expected: true);
                break;

            case "coordinate.valid":
                RequireEffect(
                    key,
                    effect,
                    WarApiMapQualityEffect.Quarantined);
                ValidateObjectProperties(
                    key,
                    parameters,
                    "minimum",
                    "maximum",
                    "requireFinite");
                RequireNumber(
                    key,
                    parameters,
                    "minimum",
                    expected: 0d);
                RequireNumber(
                    key,
                    parameters,
                    "maximum",
                    expected: 1d);
                RequireBoolean(
                    key,
                    parameters,
                    "requireFinite",
                    expected: true);
                break;

            case "source-time.representable":
                RequireEffect(
                    key,
                    effect,
                    WarApiMapQualityEffect.Suspect);
                ValidateObjectProperties(
                    key,
                    parameters,
                    "allowMissing",
                    "requireSafeUtcWhenPresent");
                RequireBoolean(
                    key,
                    parameters,
                    "allowMissing",
                    expected: true);
                RequireBoolean(
                    key,
                    parameters,
                    "requireSafeUtcWhenPresent",
                    expected: true);
                break;

            case "schema.structure-changed":
            case "taxonomy.unknown-icon":
            case "taxonomy.unknown-team":
            case "taxonomy.unknown-flag-bits":
                RequireEffect(
                    key,
                    effect,
                    WarApiMapQualityEffect.Informational);
                ValidateObjectProperties(key, parameters);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown quality policy v1 rule '{key}'.");
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> CopyParameters(
        JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Quality rule parameters must be a JSON object.");
        }

        var values = parameters
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.Ordinal);

        return new ReadOnlyDictionary<string, JsonElement>(values);
    }

    private static void ValidateObjectProperties(
        string ruleKey,
        JsonElement parameters,
        params string[] expectedNames)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Quality rule '{ruleKey}' parameters must be a JSON object.");
        }

        var actual = parameters
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var expected = expectedNames.ToHashSet(StringComparer.Ordinal);

        if (!actual.SetEquals(expected))
        {
            throw new InvalidOperationException(
                $"Quality rule '{ruleKey}' parameters do not match the v1 contract.");
        }
    }

    private static void RequireBoolean(
        string ruleKey,
        JsonElement parameters,
        string propertyName,
        bool expected)
    {
        var property = parameters.GetProperty(propertyName);
        if (property.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False) ||
            property.GetBoolean() != expected)
        {
            throw new InvalidOperationException(
                $"Quality rule '{ruleKey}' parameter '{propertyName}' must equal {expected.ToString().ToLowerInvariant()}.");
        }
    }

    private static void RequireInteger(
        string ruleKey,
        JsonElement parameters,
        string propertyName,
        int expected)
    {
        var property = parameters.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var value) ||
            value != expected)
        {
            throw new InvalidOperationException(
                $"Quality rule '{ruleKey}' parameter '{propertyName}' must equal {expected}.");
        }
    }

    private static void RequireNumber(
        string ruleKey,
        JsonElement parameters,
        string propertyName,
        double expected)
    {
        var property = parameters.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.Number ||
            !property.TryGetDouble(out var value) ||
            value != expected)
        {
            throw new InvalidOperationException(
                $"Quality rule '{ruleKey}' parameter '{propertyName}' must equal {expected.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static void RequireEffect(
        string ruleKey,
        WarApiMapQualityEffect supplied,
        WarApiMapQualityEffect expected)
    {
        if (supplied != expected)
        {
            throw new InvalidOperationException(
                $"Quality rule '{ruleKey}' must use effect '{ToStorage(expected)}'.");
        }
    }

    private static WarApiMapQualityEffect ParseEffect(
        string? value,
        string parameterName) =>
        value switch
        {
            "informational" => WarApiMapQualityEffect.Informational,
            "suspect" => WarApiMapQualityEffect.Suspect,
            "quarantined" => WarApiMapQualityEffect.Quarantined,
            _ => throw new InvalidOperationException(
                $"{parameterName} contains unsupported quality effect '{value}'."),
        };

    private static WarApiMapQualityPolicyDecision ParseDecision(
        string? value,
        string parameterName) =>
        value switch
        {
            "accepted" => WarApiMapQualityPolicyDecision.Accepted,
            "suspect" => WarApiMapQualityPolicyDecision.Suspect,
            "quarantined" => WarApiMapQualityPolicyDecision.Quarantined,
            _ => throw new InvalidOperationException(
                $"{parameterName} contains unsupported quality decision '{value}'."),
        };

    private static string ToStorage(WarApiMapQualityEffect effect) =>
        effect switch
        {
            WarApiMapQualityEffect.Informational => "informational",
            WarApiMapQualityEffect.Suspect => "suspect",
            WarApiMapQualityEffect.Quarantined => "quarantined",
            _ => throw new ArgumentOutOfRangeException(
                nameof(effect),
                effect,
                "Unknown quality effect."),
        };

    private static void ValidateRuleKey(string? value)
    {
        ValidateRequiredText(value, nameof(value));

        foreach (var character in value!)
        {
            if (!(character is >= 'a' and <= 'z' ||
                  character is >= '0' and <= '9' ||
                  character is '.' or '-'))
            {
                throw new InvalidOperationException(
                    $"Quality rule key '{value}' contains an unsupported character.");
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
}

internal sealed class WarApiMapQualityPolicyDocumentDto
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("taxonomyVersion")]
    public string? TaxonomyVersion { get; set; }

    [JsonPropertyName("reviewedAt")]
    public string? ReviewedAt { get; set; }

    [JsonPropertyName("aggregation")]
    public WarApiMapQualityAggregationDto? Aggregation { get; set; }

    [JsonPropertyName("rules")]
    public WarApiMapQualityRuleDto[]? Rules { get; set; }
}

internal sealed class WarApiMapQualityAggregationDto
{
    [JsonPropertyName("noFindingsDecision")]
    public string? NoFindingsDecision { get; set; }

    [JsonPropertyName("informationalDecision")]
    public string? InformationalDecision { get; set; }

    [JsonPropertyName("suspectDecision")]
    public string? SuspectDecision { get; set; }

    [JsonPropertyName("quarantinedDecision")]
    public string? QuarantinedDecision { get; set; }
}

internal sealed class WarApiMapQualityRuleDto
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("ruleVersion")]
    public string? RuleVersion { get; set; }

    [JsonPropertyName("configurationVersion")]
    public string? ConfigurationVersion { get; set; }

    [JsonPropertyName("effect")]
    public string? Effect { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; set; }
}
