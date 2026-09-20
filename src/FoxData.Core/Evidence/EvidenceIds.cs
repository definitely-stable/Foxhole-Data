namespace FoxData.Core.Evidence;

public readonly record struct FetchId(Guid Value)
{
    public static FetchId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct PayloadId(Guid Value)
{
    public static PayloadId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}
