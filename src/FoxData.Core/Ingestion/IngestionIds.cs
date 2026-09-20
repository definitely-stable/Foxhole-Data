namespace FoxData.Core.Ingestion;

public readonly record struct CollectionJobId(Guid Value)
{
    public static CollectionJobId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct IngestionAttemptId(Guid Value)
{
    public static IngestionAttemptId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct WorkerInstanceId(Guid Value)
{
    public static WorkerInstanceId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}
