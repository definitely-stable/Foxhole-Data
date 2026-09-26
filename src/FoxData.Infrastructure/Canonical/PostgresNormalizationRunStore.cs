using System.Data;
using FoxData.Application.Canonical;
using FoxData.Core.Evidence;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Canonical;

public sealed class PostgresNormalizationRunStore(NpgsqlDataSource dataSource)
    : INormalizationRunStore
{
    private const string Columns =
        """
        id, source_parse_run_id, normalizer_version, outcome, error_code,
        started_at, completed_at, created_at
        """;

    public async Task<NormalizationRunDescriptor?> GetAsync(
        SourceParseRunId sourceParseRunId,
        string normalizerVersion,
        CancellationToken cancellationToken)
    {
        ValidateId(sourceParseRunId);
        ValidateRequiredText(normalizerVersion, 128, nameof(normalizerVersion));

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);

        return await GetAsync(
            connection,
            transaction: null,
            sourceParseRunId,
            normalizerVersion,
            cancellationToken);
    }

    public async Task<NormalizationRunDescriptor> RecordAsync(
        NormalizationRunWrite run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        Validate(run);

        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        await EnsureSourceParseRunExistsAsync(
            connection,
            transaction,
            run.SourceParseRunId,
            cancellationToken);

        var proposedId = NormalizationRunId.New();

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            $"""
            INSERT INTO evidence.normalization_runs
                (id, source_parse_run_id, normalizer_version, outcome, error_code,
                 started_at, completed_at)
            VALUES
                (@id, @source_parse_run_id, @normalizer_version, @outcome, @error_code,
                 @started_at, @completed_at)
            ON CONFLICT (source_parse_run_id, normalizer_version)
            DO NOTHING
            RETURNING {Columns};
            """;

        AddUuid(insert, "id", proposedId.Value);
        AddUuid(insert, "source_parse_run_id", run.SourceParseRunId.Value);
        AddText(insert, "normalizer_version", run.NormalizerVersion);
        AddText(insert, "outcome", ToStorageValue(run.Outcome));
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
            run.SourceParseRunId,
            run.NormalizerVersion,
            cancellationToken)
            ?? throw new CanonicalStateIntegrityException(
                "Normalization-run uniqueness conflict was observed but the existing row was not readable.");

        EnsureEquivalent(existing, run);

        await transaction.CommitAsync(cancellationToken);
        return existing;
    }

    private static async Task<NormalizationRunDescriptor?> GetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        SourceParseRunId sourceParseRunId,
        string normalizerVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT {Columns}
            FROM evidence.normalization_runs
            WHERE source_parse_run_id = @source_parse_run_id
              AND normalizer_version = @normalizer_version;
            """;

        AddUuid(command, "source_parse_run_id", sourceParseRunId.Value);
        AddText(command, "normalizer_version", normalizerVersion);

        return await ReadAsync(command, cancellationToken);
    }

    private static async Task<NormalizationRunDescriptor?> ReadAsync(
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

        return new NormalizationRunDescriptor(
            new NormalizationRunId(reader.GetGuid(0)),
            new SourceParseRunId(reader.GetGuid(1)),
            reader.GetString(2),
            FromStorageValue(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));
    }

    private static async Task EnsureSourceParseRunExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SourceParseRunId sourceParseRunId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT 1
            FROM evidence.source_parse_runs
            WHERE id = @source_parse_run_id;
            """;

        AddUuid(command, "source_parse_run_id", sourceParseRunId.Value);

        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new CanonicalStateIntegrityException(
                $"Source parse run {sourceParseRunId} does not exist.");
        }
    }

    private static void EnsureEquivalent(
        NormalizationRunDescriptor existing,
        NormalizationRunWrite supplied)
    {
        if (existing.SourceParseRunId != supplied.SourceParseRunId ||
            !string.Equals(
                existing.NormalizerVersion,
                supplied.NormalizerVersion,
                StringComparison.Ordinal) ||
            existing.Outcome != supplied.Outcome ||
            !string.Equals(
                existing.ErrorCode,
                supplied.ErrorCode,
                StringComparison.Ordinal))
        {
            throw new CanonicalStateIntegrityException(
                "Repeated normalization-run input differs from the durable normalization run.");
        }
    }

    private static void Validate(NormalizationRunWrite run)
    {
        ValidateId(run.SourceParseRunId);
        ValidateRequiredText(
            run.NormalizerVersion,
            128,
            nameof(run.NormalizerVersion));

        if (run.ErrorCode is { Length: > 128 })
        {
            throw new ArgumentException(
                "ErrorCode must be at most 128 characters.",
                nameof(run));
        }

        if (run.ErrorCode is not null &&
            !string.Equals(run.ErrorCode, run.ErrorCode.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "ErrorCode must already be trimmed.",
                nameof(run));
        }

        if (run.CompletedAt < run.StartedAt)
        {
            throw new ArgumentException(
                "CompletedAt must not be earlier than StartedAt.",
                nameof(run));
        }
    }

    private static void ValidateId(SourceParseRunId id)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Source parse run identifier must not be empty.",
                nameof(id));
        }
    }

    private static void ValidateRequiredText(
        string value,
        int maximum,
        string name)
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

    private static string ToStorageValue(NormalizationRunOutcome outcome) =>
        outcome switch
        {
            NormalizationRunOutcome.Normalized => "normalized",
            NormalizationRunOutcome.Rejected => "rejected",
            NormalizationRunOutcome.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Unknown normalization outcome."),
        };

    private static NormalizationRunOutcome FromStorageValue(string value) =>
        value switch
        {
            "normalized" => NormalizationRunOutcome.Normalized,
            "rejected" => NormalizationRunOutcome.Rejected,
            "failed" => NormalizationRunOutcome.Failed,
            _ => throw new CanonicalStateIntegrityException(
                $"Unknown durable normalization outcome '{value}'."),
        };

    private static void AddUuid(
        NpgsqlCommand command,
        string name,
        Guid value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = value;

    private static void AddText(
        NpgsqlCommand command,
        string name,
        string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value;

    private static void AddNullableText(
        NpgsqlCommand command,
        string name,
        string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value =
            value is null ? DBNull.Value : value;

    private static void AddTimestamp(
        NpgsqlCommand command,
        string name,
        DateTimeOffset value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value = value;
}
