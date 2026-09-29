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

public readonly record struct SourceParseRunId(Guid Value)
{
    public static SourceParseRunId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct NormalizationRunId(Guid Value)
{
    public static NormalizationRunId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}


public readonly record struct CoverageObservationId(Guid Value)
{
    public static CoverageObservationId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct CoverageReprocessingRunId(Guid Value)
{
    public static CoverageReprocessingRunId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct MapSnapshotId(Guid Value)
{
    public static MapSnapshotId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct MapItemOccurrenceId(Guid Value)
{
    public static MapItemOccurrenceId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct MapTextOccurrenceId(Guid Value)
{
    public static MapTextOccurrenceId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}
