using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FoxData.Sources.WarApi;

public static class JsonStructuralFingerprinter
{
    public const string Algorithm = "json-shape@1";

    public static string Compute(ReadOnlySpan<byte> utf8Json, int maximumDepth = 64)
    {
        if (maximumDepth < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDepth),
                maximumDepth,
                "Maximum JSON depth must be positive.");
        }

        using var document = JsonDocument.Parse(
            utf8Json,
            new JsonDocumentOptions
            {
                MaxDepth = maximumDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        Visit(document.RootElement, "$", tokens);

        var canonical = string.Join(
            '\n',
            tokens.Order(StringComparer.Ordinal));

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void Visit(
        JsonElement element,
        string path,
        ISet<string> tokens)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                tokens.Add($"{path}:object");
                foreach (var property in element.EnumerateObject().OrderBy(
                    item => item.Name,
                    StringComparer.Ordinal))
                {
                    Visit(
                        property.Value,
                        $"{path}.{EscapePropertyName(property.Name)}",
                        tokens);
                }

                break;

            case JsonValueKind.Array:
                tokens.Add($"{path}:array");
                foreach (var item in element.EnumerateArray())
                {
                    Visit(item, $"{path}[]", tokens);
                }

                break;

            case JsonValueKind.String:
                tokens.Add($"{path}:string");
                break;

            case JsonValueKind.Number:
                tokens.Add(
                    element.TryGetInt64(out _)
                        ? $"{path}:integer"
                        : $"{path}:number");
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                tokens.Add($"{path}:boolean");
                break;

            case JsonValueKind.Null:
                tokens.Add($"{path}:null");
                break;

            default:
                tokens.Add($"{path}:{element.ValueKind.ToString().ToLowerInvariant()}");
                break;
        }
    }

    private static string EscapePropertyName(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(".", "\\.", StringComparison.Ordinal);
}
