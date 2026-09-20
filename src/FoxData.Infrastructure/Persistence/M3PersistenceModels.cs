namespace FoxData.Infrastructure.Persistence;

internal sealed class EndpointPollStateRow
{
    public Guid EndpointId { get; set; }
    public Guid? LastProcessedFetchId { get; set; }
    public Guid? LatestValidationFetchId { get; set; }
    public Guid? RepresentationFetchId { get; set; }
    public string? ValidatorEtag { get; set; }
    public DateTimeOffset? SourceCacheEligibleAt { get; set; }
    public DateTimeOffset? NextTargetAt { get; set; }
    public DateTimeOffset? RetryEligibleAt { get; set; }
    public DateTimeOffset? LastHttpResponseAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public required string PolicyVersion { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class SourceParseRunRow
{
    public Guid Id { get; set; }
    public Guid RepresentationFetchId { get; set; }
    public required string CapabilityKey { get; set; }
    public required string AdapterVersion { get; set; }
    public required string ParserVersion { get; set; }
    public required string FingerprintAlgorithm { get; set; }
    public string? StructuralFingerprint { get; set; }
    public required string Outcome { get; set; }
    public int UnknownPropertyCount { get; set; }
    public int UnknownCodeCount { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
