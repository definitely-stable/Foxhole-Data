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
            "dc806244ac6dac7aafcfbf7ca2da39b60de466022e7c4adcbd9751749e2af3ef",
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
