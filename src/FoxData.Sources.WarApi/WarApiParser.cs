using System.Text.Json;
using FoxData.Sources.Abstractions;

namespace FoxData.Sources.WarApi;

public enum WarApiParseOutcome
{
    Parsed,
    ParsedWithUnknowns,
    MalformedJson,
    IncompatibleShape,
}

public sealed record WarApiParseResult(
    WarApiParseOutcome Outcome,
    object? Value,
    string? StructuralFingerprint,
    int UnknownPropertyCount,
    int UnknownCodeCount,
    string? ErrorCode)
{
    public bool Parsed =>
        Outcome is WarApiParseOutcome.Parsed or WarApiParseOutcome.ParsedWithUnknowns;
}

public sealed class WarApiParser
{
    private static readonly HashSet<string> KnownTeams =
        new(StringComparer.Ordinal)
        {
            "NONE",
            "WARDENS",
            "COLONIALS",
        };

    public WarApiParseResult Parse(
        SourceCapability capability,
        ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var fingerprint = JsonStructuralFingerprinter.Compute(utf8Json);

            if (capability == WarApiCapabilities.RuntimeWarState)
            {
                var value = JsonSerializer.Deserialize(
                    utf8Json,
                    WarApiJsonContext.Default.WarApiWarStateDto)
                    ?? throw new JsonException("War state JSON deserialized to null.");

                var unknownProperties = value.ExtensionData?.Count ?? 0;
                var unknownCodes = IsKnownTeam(value.Winner) ? 0 : 1;
                return Success(value, fingerprint, unknownProperties, unknownCodes);
            }

            if (capability == WarApiCapabilities.ActiveMapList)
            {
                var value = JsonSerializer.Deserialize(
                    utf8Json,
                    WarApiJsonContext.Default.StringArray)
                    ?? throw new JsonException("Map list JSON deserialized to null.");

                return Success(value, fingerprint, 0, 0);
            }

            if (capability == WarApiCapabilities.RegionWarReport)
            {
                var value = JsonSerializer.Deserialize(
                    utf8Json,
                    WarApiJsonContext.Default.WarApiWarReportDto)
                    ?? throw new JsonException("War report JSON deserialized to null.");

                return Success(
                    value,
                    fingerprint,
                    value.ExtensionData?.Count ?? 0,
                    0);
            }

            if (capability == WarApiCapabilities.StaticMapState ||
                capability == WarApiCapabilities.DynamicMapState)
            {
                var value = JsonSerializer.Deserialize(
                    utf8Json,
                    WarApiJsonContext.Default.WarApiMapDataDto)
                    ?? throw new JsonException("Map JSON deserialized to null.");

                var unknownProperties =
                    (value.ExtensionData?.Count ?? 0) +
                    (value.MapItems?.Sum(item => item.ExtensionData?.Count ?? 0) ?? 0) +
                    (value.MapTextItems?.Sum(item => item.ExtensionData?.Count ?? 0) ?? 0);

                var unknownCodes = value.MapItems?.Sum(
                    item =>
                        (IsKnownTeam(item.TeamId) ? 0 : 1) +
                        (item.IconType is > 92 ? 1 : 0)) ?? 0;

                return Success(
                    value,
                    fingerprint,
                    unknownProperties,
                    unknownCodes);
            }

            throw new ArgumentException(
                $"Unsupported War API capability '{capability.Key}'.",
                nameof(capability));
        }
        catch (JsonException)
        {
            return new WarApiParseResult(
                WarApiParseOutcome.MalformedJson,
                null,
                null,
                0,
                0,
                "malformed_json");
        }
        catch (InvalidOperationException)
        {
            return new WarApiParseResult(
                WarApiParseOutcome.IncompatibleShape,
                null,
                null,
                0,
                0,
                "incompatible_shape");
        }
    }

    private static WarApiParseResult Success(
        object value,
        string fingerprint,
        int unknownProperties,
        int unknownCodes) =>
        new(
            unknownProperties > 0 || unknownCodes > 0
                ? WarApiParseOutcome.ParsedWithUnknowns
                : WarApiParseOutcome.Parsed,
            value,
            fingerprint,
            unknownProperties,
            unknownCodes,
            null);

    private static bool IsKnownTeam(string? value) =>
        value is null || KnownTeams.Contains(value);
}
