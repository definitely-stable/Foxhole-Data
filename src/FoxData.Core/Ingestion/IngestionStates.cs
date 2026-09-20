namespace FoxData.Core.Ingestion;

public enum CollectionJobState
{
    Pending,
    Leased,
    Processing,
    Completed,
    Failed,
    Cancelled,
}

public enum IngestionAttemptState
{
    Created,
    Fenced,
    ExchangeAuthorized,
    RawDurable,
    Completed,
    Failed,
    Uncertain,
    Superseded,
    CapturedLate,
}

public readonly record struct LeaseGeneration
{
    public LeaseGeneration(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public long Value { get; }

    public static LeaseGeneration Zero => new(0);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct FenceToken
{
    public FenceToken(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public long Value { get; }

    public static FenceToken Zero => new(0);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
