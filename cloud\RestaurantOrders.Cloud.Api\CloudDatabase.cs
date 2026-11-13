using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace RestaurantOrders.Cloud.Api;

public sealed class CloudDatabase(NpgsqlDataSource dataSource)
{
    public async Task ApplyMigrationsAsync(CancellationToken cancellationToken)
    {
        var migrationsDirectory = Path.Combine(AppContext.BaseDirectory, "Migrations");
        if (!Directory.Exists(migrationsDirectory))
            throw new DirectoryNotFoundException($"Cloud SQL migrations directory was not found: {migrationsDirectory}");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var createHistory = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS cloud_schema_migrations (
                version integer PRIMARY KEY,
                applied_at_utc timestamp with time zone NOT NULL DEFAULT now()
            );
            """, connection))
        {
            await createHistory.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var migrationPath in Directory.GetFiles(migrationsDirectory, "*.sql").Order(StringComparer.Ordinal))
        {
            var fileName = Path.GetFileNameWithoutExtension(migrationPath);
            if (!int.TryParse(fileName.Split('_')[0], out var version))
                throw new InvalidOperationException($"Migration filename must begin with a numeric version: {fileName}");

            await using var check = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM cloud_schema_migrations WHERE version = @version)", connection);
            check.Parameters.AddWithValue("version", version);
            if (await check.ExecuteScalarAsync(cancellationToken) is true)
                continue;

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var apply = new NpgsqlCommand(await File.ReadAllTextAsync(migrationPath, cancellationToken), connection, transaction))
                await apply.ExecuteNonQueryAsync(cancellationToken);

            await using (var mark = new NpgsqlCommand(
                "INSERT INTO cloud_schema_migrations(version) VALUES (@version)", connection, transaction))
            {
                mark.Parameters.AddWithValue("version", version);
                await mark.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            return await command.ExecuteScalarAsync(cancellationToken) is int value && value == 1;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    public async Task CreateStoreAsync(Guid storeId, string name, byte[] tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO cloud_stores (id, name, api_key_sha256)
            VALUES (@id, @name, @api_key_sha256)
            """, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, storeId);
        command.Parameters.AddWithValue("name", NpgsqlDbType.Text, name);
        command.Parameters.AddWithValue("api_key_sha256", NpgsqlDbType.Bytea, tokenHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Guid?> FindActiveStoreIdAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id
            FROM cloud_stores
            WHERE api_key_sha256 = @api_key_sha256 AND is_active = TRUE
            LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("api_key_sha256", NpgsqlDbType.Bytea, tokenHash);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is Guid storeId ? storeId : null;
    }

    public async Task<bool> RotateStoreTokenAsync(Guid storeId, byte[] tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE cloud_stores
            SET api_key_sha256 = @api_key_sha256
            WHERE id = @id AND is_active = TRUE
            """, connection);
        command.Parameters.AddWithValue("api_key_sha256", NpgsqlDbType.Bytea, tokenHash);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, storeId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<(int Accepted, int Duplicates)> InsertEventsAsync(
        Guid storeId,
        IReadOnlyList<SyncEventRequest?> events,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var accepted = 0;
        var duplicates = 0;

        foreach (var item in events)
        {
            var syncEvent = item!;
            await using var command = new NpgsqlCommand("""
                INSERT INTO cloud_sync_events (
                    store_id, event_id, entity_type, entity_id, event_type,
                    schema_version, occurred_at_utc, payload
                )
                VALUES (
                    @store_id, @event_id, @entity_type, @entity_id, @event_type,
                    @schema_version, @occurred_at_utc, @payload
                )
                ON CONFLICT (store_id, event_id) DO NOTHING
                RETURNING 1
                """, connection, transaction);
            command.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
            command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, syncEvent.EventId);
            command.Parameters.AddWithValue("entity_type", NpgsqlDbType.Varchar, syncEvent.EntityType!);
            command.Parameters.AddWithValue("entity_id", NpgsqlDbType.Varchar, syncEvent.EntityId!);
            command.Parameters.AddWithValue("event_type", NpgsqlDbType.Varchar, syncEvent.EventType!);
            command.Parameters.AddWithValue("schema_version", NpgsqlDbType.Integer, syncEvent.SchemaVersion);
            command.Parameters.AddWithValue("occurred_at_utc", NpgsqlDbType.TimestampTz, syncEvent.OccurredAtUtc);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, syncEvent.Payload.GetRawText());

            var inserted = await command.ExecuteScalarAsync(cancellationToken);
            if (inserted is null)
            {
                await using var compare = new NpgsqlCommand("""
                    SELECT EXISTS (
                        SELECT 1 FROM cloud_sync_events
                        WHERE store_id = @store_id
                          AND event_id = @event_id
                          AND entity_type = @entity_type
                          AND entity_id = @entity_id
                          AND event_type = @event_type
                          AND schema_version = @schema_version
                          AND occurred_at_utc = @occurred_at_utc
                          AND payload = @payload
                    )
                    """, connection, transaction);
                compare.Parameters.AddWithValue("store_id", NpgsqlDbType.Uuid, storeId);
                compare.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, syncEvent.EventId);
                compare.Parameters.AddWithValue("entity_type", NpgsqlDbType.Varchar, syncEvent.EntityType!);
                compare.Parameters.AddWithValue("entity_id", NpgsqlDbType.Varchar, syncEvent.EntityId!);
                compare.Parameters.AddWithValue("event_type", NpgsqlDbType.Varchar, syncEvent.EventType!);
                compare.Parameters.AddWithValue("schema_version", NpgsqlDbType.Integer, syncEvent.SchemaVersion);
                compare.Parameters.AddWithValue("occurred_at_utc", NpgsqlDbType.TimestampTz, syncEvent.OccurredAtUtc);
                compare.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, syncEvent.Payload.GetRawText());
                if (await compare.ExecuteScalarAsync(cancellationToken) is not true)
                    throw new SyncEventConflictException(syncEvent.EventId);

                duplicates++;
            }
            else
                accepted++;
        }

        await transaction.CommitAsync(cancellationToken);
        return (accepted, duplicates);
    }
}

public sealed class SyncEventConflictException(Guid eventId)
    : InvalidOperationException($"Sync event {eventId} was already stored with different content.");
