using System.IO.Compression;
using System.Text;
using FoxData.Sources.WarApi;

namespace FoxData.SourceTests;

public sealed class WarApiContentPolicyTests
{
    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/problem+json")]
    public void AcceptsJsonCompatibleMediaTypes(string value)
    {
        Assert.True(WarApiContentPolicy.IsJsonMediaType(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("text/json")]
    [InlineData("application/octet-stream")]
    public void RejectsNonJsonMediaTypes(string? value)
    {
        Assert.False(WarApiContentPolicy.IsJsonMediaType(value));
    }

    [Fact]
    public async Task GzipRoundTripIsBounded()
    {
        var raw = Encoding.UTF8.GetBytes("""{"warNumber":129}""");
        var compressed = await CompressGzipAsync(raw);

        var decoded = await WarApiContentPolicy.DecodeAsync(
            compressed,
            "gzip",
            maximumDecodedBytes: 1024,
            maximumExpansionRatio: 20,
            TestContext.Current.CancellationToken);

        Assert.Equal(raw, decoded);
    }

    [Fact]
    public async Task BrotliRoundTripIsBounded()
    {
        var raw = Encoding.UTF8.GetBytes("""{"mapItems":[]}""");
        var compressed = await CompressBrotliAsync(raw);

        var decoded = await WarApiContentPolicy.DecodeAsync(
            compressed,
            "br",
            maximumDecodedBytes: 1024,
            maximumExpansionRatio: 20,
            TestContext.Current.CancellationToken);

        Assert.Equal(raw, decoded);
    }

    [Fact]
    public async Task UnsupportedEncodingFailsWithoutGuessing()
    {
        await Assert.ThrowsAsync<WarApiDecodingException>(
            () => WarApiContentPolicy.DecodeAsync(
                Encoding.UTF8.GetBytes("{}"),
                "zstd",
                maximumDecodedBytes: 1024,
                maximumExpansionRatio: 20,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DecodedSizeLimitRejectsExpansion()
    {
        var raw = Encoding.UTF8.GetBytes(new string('x', 4096));
        var compressed = await CompressGzipAsync(raw);

        await Assert.ThrowsAsync<WarApiDecodingException>(
            () => WarApiContentPolicy.DecodeAsync(
                compressed,
                "gzip",
                maximumDecodedBytes: 128,
                maximumExpansionRatio: 100,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpansionRatioLimitRejectsCompressedBombShape()
    {
        var raw = Encoding.UTF8.GetBytes(new string('x', 4096));
        var compressed = await CompressGzipAsync(raw);

        await Assert.ThrowsAsync<WarApiDecodingException>(
            () => WarApiContentPolicy.DecodeAsync(
                compressed,
                "gzip",
                maximumDecodedBytes: 8192,
                maximumExpansionRatio: 2,
                TestContext.Current.CancellationToken));
    }

    private static async Task<byte[]> CompressGzipAsync(byte[] bytes)
    {
        using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await gzip.WriteAsync(bytes, TestContext.Current.CancellationToken);
        }

        return output.ToArray();
    }

    private static async Task<byte[]> CompressBrotliAsync(byte[] bytes)
    {
        using var output = new MemoryStream();
        await using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await brotli.WriteAsync(bytes, TestContext.Current.CancellationToken);
        }

        return output.ToArray();
    }
}
