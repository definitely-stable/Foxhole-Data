using FoxData.Core.Evidence;
using FoxData.Core.Sources;

namespace FoxData.Application.Canonical;

public sealed record CanonicalEvidenceInput(
    SourceParseRunId SourceParseRunId,
    FetchId RepresentationFetchId,
    PayloadId PayloadId,
    SourceId SourceId,
    ShardId ShardId,
    EndpointId EndpointId,
    string SourceKey,
    string Environment,
    string ShardKey,
    string CapabilityKey,
    string SemanticKey,
    string AdapterVersion,
    string ParserVersion,
    string ParseOutcome,
    string? ContentEncoding,
    DateTimeOffset RetrievedAt,
    ReadOnlyMemory<byte> Body);

public interface ICanonicalEvidenceReader
{
    Task<CanonicalEvidenceInput?> GetAsync(
        SourceParseRunId sourceParseRunId,
        CancellationToken cancellationToken);
}
