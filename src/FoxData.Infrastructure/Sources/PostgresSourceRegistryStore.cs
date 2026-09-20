using System.Data;
using FoxData.Application.Sources;
using FoxData.Core.Sources;
using Npgsql;
using NpgsqlTypes;

namespace FoxData.Infrastructure.Sources;

public sealed class PostgresSourceRegistryStore(NpgsqlDataSource dataSource) : ISourceRegistryStore
{
    public async Task<RegistryRegistrationResult<SourceDescriptor>> RegisterSourceAsync(
        SourceId proposedId,
        string key,
        string displayName,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO sources.sources (id, key, display_name, enabled)
            VALUES (@id, @key, @display_name, TRUE)
            ON CONFLICT (key) DO NOTHING
            RETURNING id, key, display_name, enabled, created_at, updated_at;
            """;
        AddUuid(insert, "id", proposedId.Value);
        AddText(insert, "key", key);
        AddText(insert, "display_name", displayName);

        var created = await ReadSourceAsync(insert, cancellationToken);
        if (created is not null)
        {
            return new RegistryRegistrationResult<SourceDescriptor>(
                RegistryRegistrationStatus.Created,
                created);
        }

        var existing = await GetSourceByKeyAsync(connection, key, cancellationToken)
            ?? throw new InvalidOperationException("Source uniqueness conflict was observed but the existing row was not readable.");

        var status = string.Equals(existing.DisplayName, displayName, StringComparison.Ordinal)
            ? RegistryRegistrationStatus.Existing
            : RegistryRegistrationStatus.Conflict;

        return new RegistryRegistrationResult<SourceDescriptor>(status, existing);
    }

    public async Task<RegistryRegistrationResult<ShardDescriptor>> RegisterShardAsync(
        ShardId proposedId,
        SourceId sourceId,
        string key,
        string displayName,
        string environment,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO sources.shards (id, source_id, key, display_name, environment, enabled)
            VALUES (@id, @source_id, @key, @display_name, @environment, TRUE)
            ON CONFLICT (source_id, key) DO NOTHING
            RETURNING id, source_id, key, display_name, environment, enabled, created_at, updated_at;
            """;
        AddUuid(insert, "id", proposedId.Value);
        AddUuid(insert, "source_id", sourceId.Value);
        AddText(insert, "key", key);
        AddText(insert, "display_name", displayName);
        AddText(insert, "environment", environment);

        var created = await ReadShardAsync(insert, cancellationToken);
        if (created is not null)
        {
            return new RegistryRegistrationResult<ShardDescriptor>(
                RegistryRegistrationStatus.Created,
                created);
        }

        var existing = await GetShardByKeyAsync(connection, sourceId, key, cancellationToken)
            ?? throw new InvalidOperationException("Shard uniqueness conflict was observed but the existing row was not readable.");

        var compatible =
            string.Equals(existing.DisplayName, displayName, StringComparison.Ordinal) &&
            string.Equals(existing.Environment, environment, StringComparison.Ordinal);

        return new RegistryRegistrationResult<ShardDescriptor>(
            compatible ? RegistryRegistrationStatus.Existing : RegistryRegistrationStatus.Conflict,
            existing);
    }

    public async Task<RegistryRegistrationResult<EndpointDescriptor>> RegisterEndpointAsync(
        EndpointId proposedId,
        ShardId shardId,
        string capabilityKey,
        string semanticKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO sources.endpoints (id, shard_id, capability_key, semantic_key, enabled)
            VALUES (@id, @shard_id, @capability_key, @semantic_key, TRUE)
            ON CONFLICT (shard_id, semantic_key) DO NOTHING
            RETURNING id, shard_id, capability_key, semantic_key, enabled, created_at, updated_at;
            """;
        AddUuid(insert, "id", proposedId.Value);
        AddUuid(insert, "shard_id", shardId.Value);
        AddText(insert, "capability_key", capabilityKey);
        AddText(insert, "semantic_key", semanticKey);

        var resource = await ReadEndpointAsync(insert, cancellationToken);
        var status = RegistryRegistrationStatus.Created;

        if (resource is null)
        {
            resource = await GetEndpointBySemanticKeyAsync(
                    connection,
                    transaction,
                    shardId,
                    semanticKey,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    "Endpoint uniqueness conflict was observed but the existing row was not readable.");

            status = string.Equals(resource.CapabilityKey, capabilityKey, StringComparison.Ordinal)
                ? RegistryRegistrationStatus.Existing
                : RegistryRegistrationStatus.Conflict;
        }

        if (status is not RegistryRegistrationStatus.Conflict)
        {
            await using var state = connection.CreateCommand();
            state.Transaction = transaction;
            state.CommandText =
                """
                INSERT INTO ingest.endpoint_state (endpoint_id)
                VALUES (@endpoint_id)
                ON CONFLICT (endpoint_id) DO NOTHING;
                """;
            AddUuid(state, "endpoint_id", resource.Id.Value);
            await state.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new RegistryRegistrationResult<EndpointDescriptor>(status, resource);
    }

    public async Task<SourceDescriptor?> GetSourceByKeyAsync(
        string key,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await GetSourceByKeyAsync(connection, key, cancellationToken);
    }

    public async Task<ShardDescriptor?> GetShardByKeyAsync(
        SourceId sourceId,
        string key,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await GetShardByKeyAsync(connection, sourceId, key, cancellationToken);
    }

    public async Task<ShardDescriptor?> GetShardAsync(
        ShardId shardId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, source_id, key, display_name, environment, enabled, created_at, updated_at
            FROM sources.shards
            WHERE id = @shard_id;
            """;
        AddUuid(command, "shard_id", shardId.Value);

        return await ReadShardAsync(command, cancellationToken);
    }

    public async Task<EndpointDescriptor?> GetEndpointAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, shard_id, capability_key, semantic_key, enabled, created_at, updated_at
            FROM sources.endpoints
            WHERE id = @endpoint_id;
            """;
        AddUuid(command, "endpoint_id", endpointId.Value);

        return await ReadEndpointAsync(command, cancellationToken);
    }

    public async Task<EndpointDescriptor?> GetEndpointBySemanticKeyAsync(
        ShardId shardId,
        string semanticKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await GetEndpointBySemanticKeyAsync(
            connection,
            transaction: null,
            shardId,
            semanticKey,
            cancellationToken);
    }

    private static async Task<SourceDescriptor?> GetSourceByKeyAsync(
        NpgsqlConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, key, display_name, enabled, created_at, updated_at
            FROM sources.sources
            WHERE key = @key;
            """;
        AddText(command, "key", key);

        return await ReadSourceAsync(command, cancellationToken);
    }

    private static async Task<ShardDescriptor?> GetShardByKeyAsync(
        NpgsqlConnection connection,
        SourceId sourceId,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, source_id, key, display_name, environment, enabled, created_at, updated_at
            FROM sources.shards
            WHERE source_id = @source_id
              AND key = @key;
            """;
        AddUuid(command, "source_id", sourceId.Value);
        AddText(command, "key", key);

        return await ReadShardAsync(command, cancellationToken);
    }

    private static async Task<EndpointDescriptor?> GetEndpointBySemanticKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        ShardId shardId,
        string semanticKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, shard_id, capability_key, semantic_key, enabled, created_at, updated_at
            FROM sources.endpoints
            WHERE shard_id = @shard_id
              AND semantic_key = @semantic_key;
            """;
        AddUuid(command, "shard_id", shardId.Value);
        AddText(command, "semantic_key", semanticKey);

        return await ReadEndpointAsync(command, cancellationToken);
    }

    private static async Task<SourceDescriptor?> ReadSourceAsync(
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

        return new SourceDescriptor(
            new SourceId(reader.GetGuid(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetBoolean(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5));
    }

    private static async Task<ShardDescriptor?> ReadShardAsync(
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

        return new ShardDescriptor(
            new ShardId(reader.GetGuid(0)),
            new SourceId(reader.GetGuid(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetBoolean(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));
    }

    private static async Task<EndpointDescriptor?> ReadEndpointAsync(
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

        return new EndpointDescriptor(
            new EndpointId(reader.GetGuid(0)),
            new ShardId(reader.GetGuid(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetBoolean(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6));
    }

    private static void AddUuid(NpgsqlCommand command, string name, Guid value)
    {
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = value;
    }

    private static void AddText(NpgsqlCommand command, string name, string value)
    {
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value;
    }
}
