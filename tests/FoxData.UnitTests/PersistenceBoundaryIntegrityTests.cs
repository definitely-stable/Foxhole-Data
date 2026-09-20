using FoxData.Application.Evidence;
using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;
using FoxData.Infrastructure.Evidence;
using Npgsql;

namespace FoxData.UnitTests;

public sealed class PersistenceBoundaryIntegrityTests
{
    [Fact]
    public async Task MismatchedPayloadHashIsRejectedBeforeOpeningDatabaseConnection()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=foxdata;Username=foxdata;Password=dev;Timeout=1;Command Timeout=1");
        var store = new PostgresEvidenceKernelStore(dataSource);
        var now = DateTimeOffset.UtcNow;
        var body = new byte[] { 1, 2, 3 };
        var wrongHash = PayloadHash.Compute(new byte[] { 9, 9, 9 });

        var exception = await Assert.ThrowsAsync<EvidenceIntegrityException>(
            () => store.CaptureSourceResponseAsync(
                FetchId.New(),
                PayloadId.New(),
                IngestionAttemptId.New(),
                EndpointId.New(),
                new LeaseGeneration(1),
                new FenceToken(1),
                new SourceResponseObservation(
                    now,
                    now,
                    now,
                    "fixture",
                    200,
                    "application/octet-stream",
                    null,
                    body.Length,
                    null,
                    null,
                    null,
                    1),
                wrongHash,
                body,
                priorFetchId: null,
                TestContext.Current.CancellationToken));

        Assert.Contains(
            "SHA-256",
            exception.Message,
            StringComparison.Ordinal);
    }
}
