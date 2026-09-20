namespace FoxData.Sources.Abstractions;

public readonly record struct SourceCapability(string Key)
{
    public override string ToString() => Key;
}

public sealed record SourceEndpoint(
    SourceCapability Capability,
    string SemanticKey,
    string? SourceIdentifier = null);
