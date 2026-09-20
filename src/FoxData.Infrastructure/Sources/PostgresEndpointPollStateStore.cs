using System.Data;
using FoxData.Application.Sources;
using FoxData.Core.Evidence;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Sources;

public sealed class PostgresEndpointPollStateStore(NpgsqlDataSource dataSource)
    : IEndpointPollStateStore
{
    private const string Columns =
        """
        endpoint_id, last_processed_fetch_id, latest_validation_fetch_id,
        representation_fetch_id, validator_etag, source_cache_eligible_at,
        next_target_at, retry_eligible_at, last_http_response_at, last_success_at,
        consecutive_failures, policy_version, updated_at
        """;

    public async Task<EndpointPollStateDescriptor?> GetAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken)
    {
        EnsureEndpoint(endpointId);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await GetAsync(connection, transaction: null, endpointId, cancellationToken);
    }

    public async Task<EndpointPollStateDescriptor> PutAsync(
        EndpointPollStateWrite state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureEndpointExistsAsync(
            connection,
            transaction,
            state.EndpointId,
            cancellationToken);

        await ValidateFetchReferenceAsync(
            connection,
            transaction,
            state.EndpointId,
            state.LastProcessedFetchId,
            requirePayload: false,
            "last_processed_fetch_id",
            cancellationToken);
        await ValidateFetchReferenceAsync(
            connection,
            transaction,
            state.EndpointId,
            state.LatestValidationFetchId,
            requirePayload: false,
            "latest_validation_fetch_id",
            cancellationToken);
        await ValidateFetchReferenceAsync(
            connection,
            transaction,
            state.EndpointId,
            state.RepresentationFetchId,
            requirePayload: true,
            "representation_fetch_id",
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            INSERT INTO ingest.endpoint_poll_state
                (endpoint_id, last_processed_fetch_id, latest_validation_fetch_id,
                 representation_fetch_id, validator_etag, source_cache_eligible_at,
                 next_target_at, retry_eligible_at, last_http_response_at, last_success_at,
                 consecutive_failures, policy_version)
            VALUES
                (@endpoint_id, @last_processed_fetch_id, @latest_validation_fetch_id,
                 @representation_fetch_id, @validator_etag, @source_cache_eligible_at,
                 @next_target_at, @retry_eligible_at, @last_http_response_at, @last_success_at,
                 @consecutive_failures, @policy_version)
            ON CONFLICT (endpoint_id)
            DO UPDATE SET
                last_processed_fetch_id = EXCLUDED.last_processed_fetch_id,
                latest_validation_fetch_id = EXCLUDED.latest_validation_fetch_id,
                representation_fetch_id = EXCLUDED.representation_fetch_id,
                validator_etag = EXCLUDED.validator_etag,
                source_cache_eligible_at = EXCLUDED.source_cache_eligible_at,
                next_target_at = EXCLUDED.next_target_at,
                retry_eligible_at = EXCLUDED.retry_eligible_at,
                last_http_response_at = EXCLUDED.last_http_response_at,
                last_success_at = EXCLUDED.last_success_at,
                consecutive_failures = EXCLUDED.consecutive_failures,
                policy_version = EXCLUDED.policy_version,
                updated_at = transaction_timestamp()
            RETURNING {Columns};
            """;

        AddUuid(command, "endpoint_id", state.EndpointId.Value);
        AddNullableUuid(command, "last_processed_fetch_id", state.LastProcessedFetchId?.Value);
        AddNullableUuid(command, "latest_validation_fetch_id", state.LatestValidationFetchId?.Value);
        AddNullableUuid(command, "representation_fetch_id", state.RepresentationFetchId?.Value);
        AddNullableText(command, "validator_etag", state.ValidatorEtag);
        AddNullableTimestamp(command, "source_cache_eligible_at", state.SourceCacheEligibleAt);
        AddNullableTimestamp(command, "next_target_at", state.NextTargetAt);
        AddNullableTimestamp(command, "retry_eligible_at", state.RetryEligibleAt);
        AddNullableTimestamp(command, "last_http_response_at", state.LastHttpResponseAt);
        AddNullableTimestamp(command, "last_success_at", state.LastSuccessAt);
        AddInteger(command, "consecutive_failures", state.ConsecutiveFailures);
        AddText(command, "policy_version", state.PolicyVersion);

        var written = await ReadAsync(command, cancellationToken)
            ?? throw new SourceStateIntegrityException("Poll-state upsert returned no row.");

        await transaction.CommitAsync(cancellationToken);
        return written;
    }

    private static async Task<EndpointPollStateDescriptor?> GetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        EndpointId endpointId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {Columns}
            FROM ingest.endpoint_poll_state
            WHERE endpoint_id = @endpoint_id;
            """;
        AddUuid(command, "endpoint_id", endpointId.Value);

        return await ReadAsync(command, cancellationToken);
    }

    private static async Task<EndpointPollStateDescriptor?> ReadAsync(
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

        return new EndpointPollStateDescriptor(
            new EndpointId(reader.GetGuid(0)),
            reader.IsDBNull(1) ? null : new FetchId(reader.GetGuid(1)),
            reader.IsDBNull(2) ? null : new FetchId(reader.GetGuid(2)),
            reader.IsDBNull(3) ? null : new FetchId(reader.GetGuid(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetInt32(10),
            reader.GetString(11),
            reader.GetFieldValue<DateTimeOffset>(12));
    }

    private static async Task EnsureEndpointExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EndpointId endpointId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT 1
            FROM sources.endpoints
            WHERE id = @endpoint_id;
            """;
        AddUuid(command, "endpoint_id", endpointId.Value);

        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new SourceStateIntegrityException(
                $"Endpoint {endpointId} does not exist.");
        }
    }

    private static async Task ValidateFetchReferenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EndpointId endpointId,
        FetchId? fetchId,
        bool requirePayload,
        string fieldName,
        CancellationToken cancellationToken)
    {
        if (fetchId is null)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT endpoint_id, payload_id
            FROM evidence.fetches
            WHERE id = @fetch_id;
            """;
        AddUuid(command, "fetch_id", fetchId.Value.Value);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new SourceStateIntegrityException(
                $"{fieldName} references missing fetch {fetchId}.");
        }

        if (reader.GetGuid(0) != endpointId.Value)
        {
            throw new SourceStateIntegrityException(
                $"{fieldName} references a fetch from a different endpoint.");
        }

        if (requirePayload && reader.IsDBNull(1))
        {
            throw new SourceStateIntegrityException(
                $"{fieldName} must reference a body-bearing fetch.");
        }
    }

    private static void Validate(EndpointPollStateWrite state)
    {
        EnsureEndpoint(state.EndpointId);

        if (state.ConsecutiveFailures < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state.ConsecutiveFailures,
                "ConsecutiveFailures must not be negative.");
        }

        ValidateRequiredText(state.PolicyVersion, 128, nameof(state.PolicyVersion));

        if (state.ValidatorEtag is { Length: > 1024 })
        {
            throw new ArgumentException(
                "ValidatorEtag must be at most 1024 characters.",
                nameof(state));
        }
    }

    private static void EnsureEndpoint(EndpointId endpointId)
    {
        if (endpointId.Value == Guid.Empty)
        {
            throw new ArgumentException("Endpoint identifier must not be empty.", nameof(endpointId));
        }
    }

    private static void ValidateRequiredText(string value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximum ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{name} must be non-empty, already trimmed, and at most {maximum} characters.",
                name);
        }
    }

    private static void AddUuid(NpgsqlCommand command, string name, Guid value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = value;

    private static void AddNullableUuid(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value =
            value is null ? DBNull.Value : value.Value;

    private static void AddText(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value;

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value =
            value is null ? DBNull.Value : value;

    private static void AddInteger(NpgsqlCommand command, string name, int value) =>
        command.Parameters.Add(name, NpgsqlDbType.Integer).Value = value;

    private static void AddNullableTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset? value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value =
            value is null ? DBNull.Value : value.Value;
}
