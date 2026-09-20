using FoxData.Core.Evidence;

namespace FoxData.Application.Sources;

public sealed record SourceParseRunDescriptor(
    SourceParseRunId Id,
    FetchId RepresentationFetchId,
    string CapabilityKey,
    string AdapterVersion,
    string ParserVersion,
    string FingerprintAlgorithm,
    string? StructuralFingerprint,
    string Outcome,
    int UnknownPropertyCount,
    int UnknownCodeCount,
    string? ErrorCode,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    DateTimeOffset CreatedAt);

public sealed record SourceParseRunWrite(
    FetchId RepresentationFetchId,
    string CapabilityKey,
    string AdapterVersion,
    string ParserVersion,
    string FingerprintAlgorithm,
    string? StructuralFingerprint,
    string Outcome,
    int UnknownPropertyCount,
    int UnknownCodeCount,
    string? ErrorCode,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public interface ISourceParseRunStore
{
    Task<SourceParseRunDescriptor?> GetAsync(
        FetchId representationFetchId,
        string capabilityKey,
        string parserVersion,
        CancellationToken cancellationToken);

    Task<SourceParseRunDescriptor> RecordAsync(
        SourceParseRunWrite run,
        CancellationToken cancellationToken);
}
