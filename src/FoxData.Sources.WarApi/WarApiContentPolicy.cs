using System.IO.Compression;
using System.Net.Http.Headers;

namespace FoxData.Sources.WarApi;

public sealed class WarApiDecodingException : Exception
{
    public WarApiDecodingException(string message)
        : base(message)
    {
    }
}

public static class WarApiContentPolicy
{
    public static bool IsJsonMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        if (!MediaTypeHeaderValue.TryParse(mediaType, out var parsed) ||
            parsed.MediaType is null)
        {
            return false;
        }

        if (!parsed.MediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var subtype = parsed.MediaType["application/".Length..];
        return subtype.Equals("json", StringComparison.OrdinalIgnoreCase) ||
            subtype.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<byte[]> DecodeAsync(
        ReadOnlyMemory<byte> rawBody,
        string? contentEncoding,
        int maximumDecodedBytes,
        double maximumExpansionRatio,
        CancellationToken cancellationToken = default)
    {
        if (maximumDecodedBytes < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDecodedBytes),
                maximumDecodedBytes,
                "Maximum decoded bytes must be positive.");
        }

        if (!double.IsFinite(maximumExpansionRatio) || maximumExpansionRatio < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumExpansionRatio),
                maximumExpansionRatio,
                "Maximum expansion ratio must be finite and at least 1.");
        }

        var encodings = ParseEncodings(contentEncoding);
        var current = rawBody.ToArray();

        for (var index = encodings.Count - 1; index >= 0; index--)
        {
            var encoding = encodings[index];

            if (encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            current = await DecodeOneAsync(
                current,
                encoding,
                maximumDecodedBytes,
                cancellationToken);
        }

        if (current.Length > maximumDecodedBytes)
        {
            throw new WarApiDecodingException(
                $"Decoded content exceeds {maximumDecodedBytes} bytes.");
        }

        if (rawBody.Length == 0)
        {
            if (current.Length != 0)
            {
                throw new WarApiDecodingException(
                    "Non-empty decoded content cannot originate from an empty encoded body.");
            }

            return current;
        }

        var ratio = current.Length / (double)rawBody.Length;
        if (ratio > maximumExpansionRatio)
        {
            throw new WarApiDecodingException(
                $"Decoded expansion ratio {ratio:F2} exceeds {maximumExpansionRatio:F2}.");
        }

        return current;
    }

    private static IReadOnlyList<string> ParseEncodings(string? contentEncoding)
    {
        if (string.IsNullOrWhiteSpace(contentEncoding))
        {
            return [];
        }

        var values = contentEncoding
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var value in values)
        {
            if (value.Length == 0 ||
                value.Any(character => char.IsControl(character)))
            {
                throw new WarApiDecodingException("Invalid Content-Encoding value.");
            }
        }

        return values;
    }

    private static async Task<byte[]> DecodeOneAsync(
        byte[] input,
        string encoding,
        int maximumDecodedBytes,
        CancellationToken cancellationToken)
    {
        await using var source = new MemoryStream(input, writable: false);
        await using Stream decoder = encoding.ToLowerInvariant() switch
        {
            "gzip" => new GZipStream(source, CompressionMode.Decompress, leaveOpen: false),
            "br" => new BrotliStream(source, CompressionMode.Decompress, leaveOpen: false),
            "deflate" => new DeflateStream(source, CompressionMode.Decompress, leaveOpen: false),
            _ => throw new WarApiDecodingException(
                $"Unsupported Content-Encoding '{encoding}'."),
        };

        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(81920, maximumDecodedBytes)];

        while (true)
        {
            int read;
            try
            {
                read = await decoder.ReadAsync(buffer, cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                throw new WarApiDecodingException(
                    $"Content-Encoding '{encoding}' could not be decoded: {exception.Message}");
            }

            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumDecodedBytes)
            {
                throw new WarApiDecodingException(
                    $"Decoded content exceeds {maximumDecodedBytes} bytes.");
            }

            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }
    }
}
