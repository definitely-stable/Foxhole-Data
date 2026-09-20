using System.Data;
using FoxData.Application.Sources;
using FoxData.Core.Evidence;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Sources;

public sealed class PostgresSourceParseRunStore(NpgsqlDataSource dataSource)
    : ISourceParseRunStore
{
    private const string Columns =
        """
        id, representation_fetch_id, capability_key, adapter_version, parser_version,
        fingerprint_algorithm, structural_fingerprint, outcome, unknown_property_count,
        unknown_code_count, error_code, started_at, completed_at, created_at
        """;

    public async Task<SourceParseRunDescriptor?> GetAsync(
        FetchId representationFetchId,
        string capabilityKey,
        string parserVersion,
        CancellationToken cancellationToken)
    {
        ValidateId(representationFetchId);
        ValidateRequiredText(capabilityKey, 128, nameof(capabilityKey));
        ValidateRequiredText(parserVersion, 128, nameof(parserVersion));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await GetAsync(
            connection,
            transaction: null,
            representationFetchId,
            capabilityKey,
            parserVersion,
            cancellationToken);
    }

    public async Task<SourceParseRunDescriptor> RecordAsync(
        SourceParseRunWrite run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        Validate(run);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await EnsureBodyBearingRepresentationAsync(
            connection,
            transaction,
            run.RepresentationFetchId,
            cancellationToken);

        var proposedId = SourceParseRunId.New();

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            $"""
            INSERT INTO evidence.source_parse_runs
                (id, representation_fetch_id, capability_key, adapter_version, parser_version,
                 fingerprint_algorithm, structural_fingerprint, outcome, unknown_property_count,
                 unknown_code_count, error_code, started_at, completed_at)
            VALUES
                (@id, @representation_fetch_id, @capability_key, @adapter_version, @parser_version,
                 @fingerprint_algorithm, @structural_fingerprint, @outcome, @unknown_property_count,
                 @unknown_code_count, @error_code, @started_at, @completed_at)
            ON CONFLICT (representation_fetch_id, capability_key, parser_version)
            DO NOTHING
            RETURNING {Columns};
            """;

        AddUuid(insert, "id", proposedId.Value);
        AddUuid(insert, "representation_fetch_id", run.RepresentationFetchId.Value);
        AddText(insert, "capability_key", run.CapabilityKey);
        AddText(insert, "adapter_version", run.AdapterVersion);
        AddText(insert, "parser_version", run.ParserVersion);
        AddText(insert, "fingerprint_algorithm", run.FingerprintAlgorithm);
        AddNullableText(insert, "structural_fingerprint", run.StructuralFingerprint);
        AddText(insert, "outcome", run.Outcome);
        AddInteger(insert, "unknown_property_count", run.UnknownPropertyCount);
        AddInteger(insert, "unknown_code_count", run.UnknownCodeCount);
        AddNullableText(insert, "error_code", run.ErrorCode);
        AddTimestamp(insert, "started_at", run.StartedAt);
        AddTimestamp(insert, "completed_at", run.CompletedAt);

        var inserted = await ReadAsync(insert, cancellationToken);
        if (inserted is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return inserted;
        }

        var existing = await GetAsync(
            connection,
            transaction,
            run.RepresentationFetchId,
            run.CapabilityKey,
            run.ParserVersion,
            cancellationToken)
            ?? throw new SourceStateIntegrityException(
                "Parse-run uniqueness conflict was observed but the existing row was not readable.");

        EnsureEquivalent(existing, run);

        await transaction.CommitAsync(cancellationToken);
        return existing;
    }

    private static async Task<SourceParseRunDescriptor?> GetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        FetchId representationFetchId,
        string capabilityKey,
        string parserVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {Columns}
            FROM evidence.source_parse_runs
            WHERE representation_fetch_id = @representation_fetch_id
              AND capability_key = @capability_key
              AND parser_version = @parser_version;
            """;
        AddUuid(command, "representation_fetch_id", representationFetchId.Value);
        AddText(command, "capability_key", capabilityKey);
        AddText(command, "parser_version", parserVersion);

        return await ReadAsync(command, cancellationToken);
    }

    private static async Task<SourceParseRunDescriptor?> ReadAsync(
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

        return new SourceParseRunDescriptor(
            new SourceParseRunId(reader.GetGuid(0)),
            new FetchId(reader.GetGuid(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetFieldValue<DateTimeOffset>(11),
            reader.GetFieldValue<DateTimeOffset>(12),
            reader.GetFieldValue<DateTimeOffset>(13));
    }

    private static async Task EnsureBodyBearingRepresentationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FetchId fetchId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT payload_id
            FROM evidence.fetches
            WHERE id = @fetch_id;
            """;
        AddUuid(command, "fetch_id", fetchId.Value);

        var payloadId = await command.ExecuteScalarAsync(cancellationToken);
        if (payloadId is not Guid)
        {
            throw new SourceStateIntegrityException(
                $"Representation fetch {fetchId} is missing or has no payload.");
        }
    }

    private static void EnsureEquivalent(
        SourceParseRunDescriptor existing,
        SourceParseRunWrite supplied)
    {
        if (existing.RepresentationFetchId != supplied.RepresentationFetchId ||
            !string.Equals(existing.CapabilityKey, supplied.CapabilityKey, StringComparison.Ordinal) ||
            !string.Equals(existing.AdapterVersion, supplied.AdapterVersion, StringComparison.Ordinal) ||
            !string.Equals(existing.ParserVersion, supplied.ParserVersion, StringComparison.Ordinal) ||
            !string.Equals(existing.FingerprintAlgorithm, supplied.FingerprintAlgorithm, StringComparison.Ordinal) ||
            !string.Equals(existing.StructuralFingerprint, supplied.StructuralFingerprint, StringComparison.Ordinal) ||
            !string.Equals(existing.Outcome, supplied.Outcome, StringComparison.Ordinal) ||
            existing.UnknownPropertyCount != supplied.UnknownPropertyCount ||
            existing.UnknownCodeCount != supplied.UnknownCodeCount ||
            !string.Equals(existing.ErrorCode, supplied.ErrorCode, StringComparison.Ordinal) ||
            !SamePostgresTimestamp(existing.StartedAt, supplied.StartedAt) ||
            !SamePostgresTimestamp(existing.CompletedAt, supplied.CompletedAt))
        {
            throw new SourceStateIntegrityException(
                "Repeated parse-run input differs from the durable parse run.");
        }
    }

    private static bool SamePostgresTimestamp(
        DateTimeOffset first,
        DateTimeOffset second) =>
        first.UtcTicks / 10 == second.UtcTicks / 10;

    private static void Validate(SourceParseRunWrite run)
    {
        ValidateId(run.RepresentationFetchId);
        ValidateRequiredText(run.CapabilityKey, 128, nameof(run.CapabilityKey));
        ValidateRequiredText(run.AdapterVersion, 128, nameof(run.AdapterVersion));
        ValidateRequiredText(run.ParserVersion, 128, nameof(run.ParserVersion));
        ValidateRequiredText(run.FingerprintAlgorithm, 64, nameof(run.FingerprintAlgorithm));
        ValidateRequiredText(run.Outcome, 64, nameof(run.Outcome));

        if (run.StructuralFingerprint is { Length: > 128 })
        {
            throw new ArgumentException(
                "StructuralFingerprint must be at most 128 characters.",
                nameof(run));
        }

        if (run.ErrorCode is { Length: > 128 })
        {
            throw new ArgumentException(
                "ErrorCode must be at most 128 characters.",
                nameof(run));
        }

        if (run.UnknownPropertyCount < 0 || run.UnknownCodeCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(run),
                "Unknown counters must not be negative.");
        }

        if (run.CompletedAt < run.StartedAt)
        {
            throw new ArgumentException(
                "CompletedAt must not be earlier than StartedAt.",
                nameof(run));
        }
    }

    private static void ValidateId(FetchId id)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("Fetch identifier must not be empty.", nameof(id));
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

    private static void AddText(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value;

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value =
            value is null ? DBNull.Value : value;

    private static void AddInteger(NpgsqlCommand command, string name, int value) =>
        command.Parameters.Add(name, NpgsqlDbType.Integer).Value = value;

    private static void AddTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value = value;
}
