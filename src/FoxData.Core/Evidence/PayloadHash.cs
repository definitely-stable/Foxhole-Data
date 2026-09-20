using System.Security.Cryptography;

namespace FoxData.Core.Evidence;

public readonly record struct PayloadHash
{
    private const int Sha256ByteLength = 32;
    private const int Sha256HexLength = Sha256ByteLength * 2;

    private PayloadHash(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static PayloadHash Compute(ReadOnlySpan<byte> bytes)
    {
        var hash = SHA256.HashData(bytes);
        return new PayloadHash(Convert.ToHexString(hash).ToLowerInvariant());
    }

    public static PayloadHash Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length != Sha256HexLength)
        {
            throw new FormatException("Payload hash must contain exactly 64 hexadecimal characters.");
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromHexString(value);
        }
        catch (FormatException exception)
        {
            throw new FormatException("Payload hash must be valid hexadecimal.", exception);
        }

        if (decoded.Length != Sha256ByteLength)
        {
            throw new FormatException("Payload hash must decode to exactly 32 bytes.");
        }

        return new PayloadHash(value.ToLowerInvariant());
    }

    public byte[] ToByteArray() => Convert.FromHexString(Value);

    public override string ToString() => Value;
}
