using System.Text;
using FoxData.Core.Evidence;

namespace FoxData.UnitTests;

public sealed class PayloadHashTests
{
    [Fact]
    public void ComputeProducesStableLowercaseSha256()
    {
        var hash = PayloadHash.Compute(Encoding.UTF8.GetBytes("foxdata"));

        Assert.Equal(
            "9604d385997aa9b60f54b810f81923883817405125659ea04e6af11f8fe13c90",
            hash.Value);
        Assert.Equal(hash, PayloadHash.Parse(hash.Value.ToUpperInvariant()));
        Assert.Equal(32, hash.ToByteArray().Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abcd")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void ParseRejectsMalformedHash(string value)
    {
        Assert.Throws<FormatException>(() => PayloadHash.Parse(value));
    }
}
