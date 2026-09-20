namespace FoxData.Core.Sources;

public readonly record struct SourceId(Guid Value)
{
    public static SourceId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ShardId(Guid Value)
{
    public static ShardId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct EndpointId(Guid Value)
{
    public static EndpointId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}
