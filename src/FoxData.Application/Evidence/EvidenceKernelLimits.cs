namespace FoxData.Application.Evidence;

public sealed record EvidenceKernelLimits
{
    public const long DefaultMaxPayloadBytes = 64L * 1024L * 1024L;
    public const long MaximumMaxPayloadBytes = 1024L * 1024L * 1024L;

    public EvidenceKernelLimits(long maxPayloadBytes = DefaultMaxPayloadBytes)
    {
        if (maxPayloadBytes is < 1 or > MaximumMaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPayloadBytes),
                maxPayloadBytes,
                $"Max payload bytes must be between 1 and {MaximumMaxPayloadBytes}.");
        }

        MaxPayloadBytes = maxPayloadBytes;
    }

    public long MaxPayloadBytes { get; }
}
