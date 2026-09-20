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
    DateTimeOffset CreatedAt,
    long? SourceVersion,
    long? SourceLastUpdated);

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
    DateTimeOffset CompletedAt,
    long? SourceVersion = null,
    long? SourceLastUpdated = null);

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
