using System.Data;
using FoxData.Application.Evidence;
using FoxData.Core.Evidence;
using FoxData.Core.Ingestion;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Evidence;

public sealed class PostgresEndpointEvidenceReader(NpgsqlDataSource dataSource)
    : IEndpointEvidenceReader
{
    private const string FetchColumns =
        """
        id, attempt_id, endpoint_id, request_started_at, response_started_at, retrieved_at,
        transport_kind, status_code, media_type, content_encoding, declared_length, source_etag,
        cache_control, expires_at, payload_id, prior_fetch_id, duration_ms, created_at,
        source_date, source_age_seconds, retry_after, body_error_code
        """;

    public async Task<EndpointEvidenceSnapshot?> GetCurrentAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken)
    {
        if (endpointId.Value == Guid.Empty)
        {
            throw new ArgumentException("Endpoint identifier must not be empty.", nameof(endpointId));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var attemptId = await GetCurrentAttemptIdAsync(
            connection,
            endpointId,
            cancellationToken);

        if (attemptId is null)
        {
            return null;
        }

        var current = await GetFetchByAttemptAsync(
            connection,
            attemptId.Value,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Endpoint {endpointId} points to attempt {attemptId} without durable fetch evidence.");

        FetchDescriptor? representation = null;
        PayloadDescriptor? payload = null;

        var representationId = current.PayloadId is not null
            ? current.Id
            : current.PriorFetchId;

        if (representationId is { } fetchId)
        {
            representation = fetchId == current.Id
                ? current
                : await GetFetchAsync(connection, fetchId, cancellationToken);

            if (representation is null ||
                representation.EndpointId != endpointId ||
                representation.PayloadId is null)
            {
                throw new InvalidOperationException(
                    $"Endpoint {endpointId} has invalid representation lineage from fetch {current.Id}.");
            }

            payload = await GetPayloadAsync(
                connection,
                representation.PayloadId.Value,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Representation fetch {representation.Id} references missing payload.");
        }

        return new EndpointEvidenceSnapshot(
            endpointId,
            current,
            representation,
            payload);
    }

    private static async Task<IngestionAttemptId?> GetCurrentAttemptIdAsync(
        NpgsqlConnection connection,
        EndpointId endpointId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT last_current_capture_attempt_id
            FROM ingest.endpoint_state
            WHERE endpoint_id = @endpoint_id;
            """;
        AddUuid(command, "endpoint_id", endpointId.Value);

        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is Guid attemptId
            ? new IngestionAttemptId(attemptId)
            : null;
    }

    private static async Task<FetchDescriptor?> GetFetchByAttemptAsync(
        NpgsqlConnection connection,
        IngestionAttemptId attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {FetchColumns}
            FROM evidence.fetches
            WHERE attempt_id = @attempt_id;
            """;
        AddUuid(command, "attempt_id", attemptId.Value);

        return await ReadFetchAsync(command, cancellationToken);
    }

    private static async Task<FetchDescriptor?> GetFetchAsync(
        NpgsqlConnection connection,
        FetchId fetchId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {FetchColumns}
            FROM evidence.fetches
            WHERE id = @fetch_id;
            """;
        AddUuid(command, "fetch_id", fetchId.Value);

        return await ReadFetchAsync(command, cancellationToken);
    }

    private static async Task<PayloadDescriptor?> GetPayloadAsync(
        NpgsqlConnection connection,
        PayloadId payloadId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, sha256, byte_length, body, created_at
            FROM evidence.payloads
            WHERE id = @payload_id;
            """;
        AddUuid(command, "payload_id", payloadId.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var bytes = reader.GetFieldValue<byte[]>(1);

        return new PayloadDescriptor(
            new PayloadId(reader.GetGuid(0)),
            PayloadHash.Parse(Convert.ToHexString(bytes).ToLowerInvariant()),
            reader.GetInt64(2),
            reader.GetFieldValue<byte[]>(3),
            reader.GetFieldValue<DateTimeOffset>(4));
    }

    private static async Task<FetchDescriptor?> ReadFetchAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FetchDescriptor(
            new FetchId(reader.GetGuid(0)),
            new IngestionAttemptId(reader.GetGuid(1)),
            new EndpointId(reader.GetGuid(2)),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
            reader.IsDBNull(14) ? null : new PayloadId(reader.GetGuid(14)),
            reader.IsDBNull(15) ? null : new FetchId(reader.GetGuid(15)),
            reader.GetInt64(16),
            reader.GetFieldValue<DateTimeOffset>(17),
            reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18),
            reader.IsDBNull(19) ? null : reader.GetInt64(19),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.GetString(21));
    }

    private static void AddUuid(NpgsqlCommand command, string name, Guid value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = value;
}
