namespace FoxData.Core.Quality;

public readonly record struct MapQualityRunId(Guid Value)
{
    public static MapQualityRunId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct MapQualityFindingId(Guid Value)
{
    public static MapQualityFindingId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}
