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


public readonly record struct RegionId(Guid Value)
{
    public static RegionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct WarRegionId(Guid Value)
{
    public static WarRegionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}


public readonly record struct WarReportObservationId(Guid Value)
{
    public static WarReportObservationId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct MapObservationId(Guid Value)
{
    public static MapObservationId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}
