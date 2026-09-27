namespace FoxData.Core.Runtime;

public readonly record struct WarId(Guid Value)
{
    public static WarId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct WarObservationId(Guid Value)
{
    public static WarObservationId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}
