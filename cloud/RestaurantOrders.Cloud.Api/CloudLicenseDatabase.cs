using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace RestaurantOrders.Cloud.Api;

public sealed class CloudLicenseDatabase(NpgsqlDataSource dataSource)
{
    public async Task<bool> RegisterInstallationAsync(
        Guid installationId, string machineName, string macAddress, byte[] clientTokenHash, CancellationToken cancellationToken)
    {
        var macHash = HashMac(macAddress);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO cloud_license_installations (installation_id, machine_name, mac_address_sha256, client_token_sha256)
            VALUES (@installation_id, @machine_name, @mac_hash, @client_token_hash)
            ON CONFLICT (installation_id) DO UPDATE
            SET last_seen_at_utc = now(),
                machine_name = EXCLUDED.machine_name,
                mac_address_sha256 = EXCLUDED.mac_address_sha256,
                client_token_sha256 = EXCLUDED.client_token_sha256,
                rebind_pending = false
            WHERE cloud_license_installations.rebind_pending = true
               OR (
                   cloud_license_installations.machine_name = EXCLUDED.machine_name
                   AND cloud_license_installations.mac_address_sha256 = EXCLUDED.mac_address_sha256
                   AND cloud_license_installations.client_public_key IS NULL
                   AND (cloud_license_installations.client_token_sha256 IS NULL
                        OR cloud_license_installations.client_token_sha256 = EXCLUDED.client_token_sha256)
               )
            RETURNING installation_id
            """, connection);
        command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
        command.Parameters.AddWithValue("machine_name", NpgsqlDbType.Varchar, machineName);
        command.Parameters.AddWithValue("mac_hash", NpgsqlDbType.Bytea, macHash);
        command.Parameters.AddWithValue("client_token_hash", NpgsqlDbType.Bytea, clientTokenHash);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid;
    }

    public async Task<bool> RegisterSignedInstallationRequestAsync(
        Guid installationId,
        string machineName,
        string macAddress,
        byte[] clientPublicKey,
        byte[] nonceHash,
        CancellationToken cancellationToken)
    {
        var macHash = HashMac(macAddress);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var register = new NpgsqlCommand("""
            INSERT INTO cloud_license_installations (
                installation_id, machine_name, mac_address_sha256, client_token_sha256, client_public_key)
            VALUES (@installation_id, @machine_name, @mac_hash, NULL, @client_public_key)
            ON CONFLICT (installation_id) DO UPDATE
            SET last_seen_at_utc = now(),
                machine_name = EXCLUDED.machine_name,
                mac_address_sha256 = EXCLUDED.mac_address_sha256,
                client_token_sha256 = NULL,
                client_public_key = EXCLUDED.client_public_key,
                rebind_pending = false
            WHERE cloud_license_installations.rebind_pending = true
               OR (
                   cloud_license_installations.machine_name = EXCLUDED.machine_name
                   AND cloud_license_installations.mac_address_sha256 = EXCLUDED.mac_address_sha256
                   AND (cloud_license_installations.client_public_key IS NULL
                        OR cloud_license_installations.client_public_key = EXCLUDED.client_public_key)
               )
            RETURNING installation_id
            """, connection, transaction))
        {
            register.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
            register.Parameters.AddWithValue("machine_name", NpgsqlDbType.Varchar, machineName);
            register.Parameters.AddWithValue("mac_hash", NpgsqlDbType.Bytea, macHash);
            register.Parameters.AddWithValue("client_public_key", NpgsqlDbType.Bytea, clientPublicKey);
            if (await register.ExecuteScalarAsync(cancellationToken) is not Guid)
                return false;
        }

        try
        {
            await using var nonce = new NpgsqlCommand("""
                INSERT INTO cloud_license_request_nonces (installation_id, nonce_sha256)
                VALUES (@installation_id, @nonce_hash)
                """, connection, transaction);
            nonce.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
            nonce.Parameters.AddWithValue("nonce_hash", NpgsqlDbType.Bytea, nonceHash);
            await nonce.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }

        await using (var cleanup = new NpgsqlCommand("""
            DELETE FROM cloud_license_request_nonces
            WHERE received_at_utc < now() - interval '10 minutes'
            """, connection, transaction))
            await cleanup.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RequestInstallationRebindAsync(Guid installationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid? licenseId;
        await using (var command = new NpgsqlCommand("""
            UPDATE cloud_license_installations
            SET rebind_pending = true
            WHERE installation_id = @installation_id
            RETURNING license_id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is null) return false;
            licenseId = value is Guid id ? id : null;
        }

        await AddAuditAsync(connection, transaction, licenseId, installationId, "rebind-requested", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<LicenseActivationInfo?> GetActivationInfoAsync(Guid installationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT i.license_id, i.is_assigned,
                   c.shop_name, c.vat_number,
                   CASE WHEN c.is_active THEN l.status ELSE 'revoked' END,
                   l.expires_at_utc
            FROM cloud_license_installations i
            LEFT JOIN cloud_licenses l ON l.id = i.license_id
            LEFT JOIN cloud_license_customers c ON c.id = l.customer_id
            WHERE i.installation_id = @installation_id
            """, connection);
        command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new LicenseActivationInfo(
            reader.IsDBNull(0) ? null : reader.GetGuid(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? "" : reader.GetString(2),
            reader.IsDBNull(3) ? "" : reader.GetString(3),
            reader.IsDBNull(4) ? "" : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetDateTime(5));
    }

    public async Task<Guid> CreateLicenseAsync(
        string customerName, string vatNumber, DateTime? expiresAtUtc, int maxInstallations, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var customer = new NpgsqlCommand("""
            INSERT INTO cloud_license_customers (id, store_identifier, shop_name, vat_number)
            VALUES (@id, @store_identifier, @shop_name, @vat_number)
            """, connection, transaction))
        {
            customer.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
            customer.Parameters.AddWithValue("store_identifier", NpgsqlDbType.Varchar, $"LEGACY-{id:N}");
            customer.Parameters.AddWithValue("shop_name", NpgsqlDbType.Varchar, customerName);
            customer.Parameters.AddWithValue("vat_number", NpgsqlDbType.Varchar, vatNumber);
            await customer.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var license = new NpgsqlCommand("""
            INSERT INTO cloud_licenses (id, customer_id, customer_name, vat_number, expires_at_utc, max_installations)
            VALUES (@id, @customer_id, @customer_name, @vat_number, @expires_at_utc, @max_installations)
            """, connection, transaction))
        {
            license.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
            license.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, id);
            license.Parameters.AddWithValue("customer_name", NpgsqlDbType.Varchar, customerName);
            license.Parameters.AddWithValue("vat_number", NpgsqlDbType.Varchar, vatNumber);
            AddTimestamp(license, "expires_at_utc", expiresAtUtc);
            license.Parameters.AddWithValue("max_installations", NpgsqlDbType.Integer, maxInstallations);
            await license.ExecuteNonQueryAsync(cancellationToken);
        }
        await AddAuditAsync(connection, transaction, id, null, "created", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<Guid> CreateCategoryAsync(string name, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO cloud_license_categories (id, name)
            VALUES (@id, @name)
            """, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
        command.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, name);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    public async Task<bool> UpdateCategoryAsync(Guid categoryId, string name, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE cloud_license_categories
            SET name = @name, updated_at_utc = now()
            WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, categoryId);
        command.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, name);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("DELETE FROM cloud_license_categories WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, categoryId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<Guid> CreateCustomerAsync(
        string storeIdentifier, string shopName, string resellerName, string vatNumber, Guid? categoryId,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO cloud_license_customers (id, store_identifier, shop_name, reseller_name, vat_number, category_id)
            VALUES (@id, @store_identifier, @shop_name, @reseller_name, @vat_number, @category_id)
            """, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
        command.Parameters.AddWithValue("store_identifier", NpgsqlDbType.Varchar, storeIdentifier);
        command.Parameters.AddWithValue("shop_name", NpgsqlDbType.Varchar, shopName);
        command.Parameters.AddWithValue("reseller_name", NpgsqlDbType.Varchar, resellerName);
        command.Parameters.AddWithValue("vat_number", NpgsqlDbType.Varchar, vatNumber);
        command.Parameters.AddWithValue("category_id", NpgsqlDbType.Uuid, categoryId is null ? DBNull.Value : categoryId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AddAuditAsync(null, null, "customer-created", cancellationToken, id);
        return id;
    }

    public async Task<bool> UpdateCustomerAsync(
        Guid customerId, string storeIdentifier, string shopName, string resellerName, string vatNumber, Guid? categoryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE cloud_license_customers
            SET store_identifier = @store_identifier,
                shop_name = @shop_name,
                reseller_name = @reseller_name,
                vat_number = @vat_number,
                category_id = @category_id,
                updated_at_utc = now()
            WHERE id = @id AND is_active = true
            """, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, customerId);
        command.Parameters.AddWithValue("store_identifier", NpgsqlDbType.Varchar, storeIdentifier);
        command.Parameters.AddWithValue("shop_name", NpgsqlDbType.Varchar, shopName);
        command.Parameters.AddWithValue("reseller_name", NpgsqlDbType.Varchar, resellerName);
        command.Parameters.AddWithValue("vat_number", NpgsqlDbType.Varchar, vatNumber);
        command.Parameters.AddWithValue("category_id", NpgsqlDbType.Uuid, categoryId is null ? DBNull.Value : categoryId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await using var syncLegacyFields = new NpgsqlCommand("""
            UPDATE cloud_licenses
            SET customer_name = @shop_name, vat_number = @vat_number, updated_at_utc = now()
            WHERE customer_id = @id
            """, connection);
        syncLegacyFields.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, customerId);
        syncLegacyFields.Parameters.AddWithValue("shop_name", NpgsqlDbType.Varchar, shopName);
        syncLegacyFields.Parameters.AddWithValue("vat_number", NpgsqlDbType.Varchar, vatNumber);
        await syncLegacyFields.ExecuteNonQueryAsync(cancellationToken);
        await AddAuditAsync(null, null, "customer-updated", cancellationToken, customerId);
        return true;
    }

    public async Task<bool> DeleteCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCustomer = new NpgsqlCommand("""
            SELECT id FROM cloud_license_customers WHERE id = @id AND is_active = true FOR UPDATE
            """, connection, transaction))
        {
            lockCustomer.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, customerId);
            if (await lockCustomer.ExecuteScalarAsync(cancellationToken) is not Guid)
                return false;
        }

        await using (var audit = new NpgsqlCommand("""
            INSERT INTO cloud_license_audit (license_id, installation_id, customer_id, action)
            SELECT l.id, i.installation_id, l.customer_id, 'customer-deleted'
            FROM cloud_licenses l
            LEFT JOIN cloud_license_installations i ON i.license_id = l.id AND i.is_assigned = true
            WHERE l.customer_id = @customer_id
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var revoke = new NpgsqlCommand("""
            UPDATE cloud_licenses SET status = 'revoked', updated_at_utc = now()
            WHERE customer_id = @customer_id
            """, connection, transaction))
        {
            revoke.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var delete = new NpgsqlCommand("""
            UPDATE cloud_license_customers
            SET is_active = false, deleted_at_utc = now(), updated_at_utc = now()
            WHERE id = @id
            """, connection, transaction))
        {
            delete.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, customerId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> PermanentlyDeleteCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCustomer = new NpgsqlCommand("""
            SELECT id FROM cloud_license_customers WHERE id = @id FOR UPDATE
            """, connection, transaction))
        {
            lockCustomer.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, customerId);
            if (await lockCustomer.ExecuteScalarAsync(cancellationToken) is not Guid)
                return false;
        }

        await using (var deleteAudit = new NpgsqlCommand("""
            DELETE FROM cloud_license_audit
            WHERE customer_id = @customer_id OR license_id IN (
                SELECT id FROM cloud_licenses WHERE customer_id = @customer_id
            ) OR installation_id IN (
                SELECT installation_id
                FROM cloud_license_installations
                WHERE license_id IN (
                    SELECT id FROM cloud_licenses WHERE customer_id = @customer_id
                )
            )
            """, connection, transaction))
        {
            deleteAudit.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
            await deleteAudit.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteInstallations = new NpgsqlCommand("""
            DELETE FROM cloud_license_installations
            WHERE license_id IN (
                SELECT id FROM cloud_licenses WHERE customer_id = @customer_id
            )
            """, connection, transaction))
        {
            deleteInstallations.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
            await deleteInstallations.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteLicenses = new NpgsqlCommand("""
            DELETE FROM cloud_licenses WHERE customer_id = @customer_id
            """, connection, transaction))
        {
            deleteLicenses.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
            await deleteLicenses.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteCustomer = new NpgsqlCommand("""
            DELETE FROM cloud_license_customers WHERE id = @customer_id
            """, connection, transaction))
        {
            deleteCustomer.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
            if (await deleteCustomer.ExecuteNonQueryAsync(cancellationToken) != 1)
                return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RestoreCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE cloud_license_customers
            SET is_active = true, deleted_at_utc = NULL, updated_at_utc = now()
            WHERE id = @id AND is_active = false
            """, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, customerId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await AddAuditAsync(null, null, "customer-restored", cancellationToken, customerId);
        return true;
    }

    public async Task<Guid> CreateLicenseEditionAsync(
        string name, int maxInstallations, int defaultDurationMonths, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO cloud_license_editions (id, name, max_installations, default_duration_months)
            VALUES (@id, @name, @max_installations, @default_duration_months)
            """, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
        command.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, name);
        command.Parameters.AddWithValue("max_installations", NpgsqlDbType.Integer, maxInstallations);
        command.Parameters.AddWithValue("default_duration_months", NpgsqlDbType.Integer, defaultDurationMonths);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    public async Task<bool> UpdateLicenseEditionAsync(
        Guid editionId, string name, int maxInstallations, int defaultDurationMonths, bool isActive,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE cloud_license_editions
            SET name = @name, max_installations = @max_installations,
                default_duration_months = @default_duration_months, is_active = @is_active,
                updated_at_utc = now()
            WHERE id = @id
            """, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, editionId);
        command.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, name);
        command.Parameters.AddWithValue("max_installations", NpgsqlDbType.Integer, maxInstallations);
        command.Parameters.AddWithValue("default_duration_months", NpgsqlDbType.Integer, defaultDurationMonths);
        command.Parameters.AddWithValue("is_active", NpgsqlDbType.Boolean, isActive);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<(Guid? LicenseId, string? Error)> CreateCustomerLicenseAsync(
        Guid customerId, Guid editionId, DateTime expiresAtUtc, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        int maxInstallations;
        string customerName;
        string vatNumber;
        await using (var lookup = new NpgsqlCommand("""
            SELECT e.max_installations, c.shop_name, c.vat_number
            FROM cloud_license_editions e
            CROSS JOIN cloud_license_customers c
            WHERE e.id = @edition_id AND e.is_active = true
              AND c.id = @customer_id AND c.is_active = true
            FOR UPDATE OF e, c
            """, connection, transaction))
        {
            lookup.Parameters.AddWithValue("edition_id", NpgsqlDbType.Uuid, editionId);
            lookup.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.DisposeAsync();
                await transaction.RollbackAsync(cancellationToken);
                return (null, "customer-or-edition-not-found");
            }
            maxInstallations = reader.GetInt32(0);
            customerName = reader.GetString(1);
            vatNumber = reader.GetString(2);
        }

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO cloud_licenses (id, customer_id, edition_id, customer_name, vat_number,
                expires_at_utc, max_installations)
            VALUES (@id, @customer_id, @edition_id, @customer_name, @vat_number,
                @expires_at_utc, @max_installations)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
            insert.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
            insert.Parameters.AddWithValue("edition_id", NpgsqlDbType.Uuid, editionId);
            insert.Parameters.AddWithValue("customer_name", NpgsqlDbType.Varchar, customerName);
            insert.Parameters.AddWithValue("vat_number", NpgsqlDbType.Varchar, vatNumber);
            AddTimestamp(insert, "expires_at_utc", expiresAtUtc);
            insert.Parameters.AddWithValue("max_installations", NpgsqlDbType.Integer, maxInstallations);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await AddAuditAsync(connection, transaction, id, null, "created", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (id, null);
    }

    public async Task<string?> UpdateLicenseTermsAsync(
        Guid licenseId, Guid editionId, DateTime expiresAtUtc, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        int maxInstallations;
        await using (var edition = new NpgsqlCommand("""
            SELECT max_installations FROM cloud_license_editions WHERE id = @id AND is_active = true
            """, connection, transaction))
        {
            edition.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, editionId);
            var value = await edition.ExecuteScalarAsync(cancellationToken);
            if (value is not int limit)
                return "edition-not-found";
            maxInstallations = limit;
        }

        await using (var count = new NpgsqlCommand("""
            SELECT count(*) FROM cloud_license_installations
            WHERE license_id = @license_id AND is_assigned = true
            """, connection, transaction))
        {
            count.Parameters.AddWithValue("license_id", NpgsqlDbType.Uuid, licenseId);
            if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)) > maxInstallations)
                return "installation-limit";
        }

        await using var update = new NpgsqlCommand("""
            UPDATE cloud_licenses l
            SET edition_id = @edition_id, max_installations = @max_installations,
                expires_at_utc = @expires_at_utc, status = 'active', updated_at_utc = now()
            FROM cloud_license_customers c
            WHERE l.id = @id AND l.customer_id = c.id AND c.is_active = true
            """, connection, transaction);
        update.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, licenseId);
        update.Parameters.AddWithValue("edition_id", NpgsqlDbType.Uuid, editionId);
        update.Parameters.AddWithValue("max_installations", NpgsqlDbType.Integer, maxInstallations);
        AddTimestamp(update, "expires_at_utc", expiresAtUtc);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            return "license-not-found";

        await AddAuditAsync(connection, transaction, licenseId, null, "terms-updated", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return null;
    }

    public async Task<bool> RenewLicenseAsync(Guid licenseId, DateTime? expiresAtUtc, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE cloud_licenses l
            SET expires_at_utc = @expires_at_utc, status = 'active', updated_at_utc = now()
            FROM cloud_license_customers c
            WHERE l.id = @id AND l.customer_id = c.id AND c.is_active = true
            """, connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, licenseId);
        AddTimestamp(command, "expires_at_utc", expiresAtUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await AddAuditAsync(connection, transaction, licenseId, null, "renewed", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RevokeLicenseAsync(Guid licenseId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE cloud_licenses SET status = 'revoked', updated_at_utc = now() WHERE id = @id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, licenseId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await AddAuditAsync(connection, transaction, licenseId, null, "revoked", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<AssignInstallationResult> AssignInstallationAsync(
        Guid installationId, Guid licenseId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var lockLicense = new NpgsqlCommand("""
            SELECT l.max_installations, l.status, l.expires_at_utc, c.is_active
            FROM cloud_licenses l
            JOIN cloud_license_customers c ON c.id = l.customer_id
            WHERE l.id = @license_id FOR UPDATE OF l
            """, connection, transaction);
        lockLicense.Parameters.AddWithValue("license_id", NpgsqlDbType.Uuid, licenseId);
        int maxInstallations;
        await using (var reader = await lockLicense.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return AssignInstallationResult.LicenseNotFound;
            maxInstallations = reader.GetInt32(0);
            var status = reader.GetString(1);
            DateTime? expiresAtUtc = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
            var customerIsActive = reader.GetBoolean(3);
            if (!customerIsActive || status != "active" || (expiresAtUtc is not null && expiresAtUtc <= DateTime.UtcNow))
                return AssignInstallationResult.LicenseInactive;
        }

        await using var lockInstallation = new NpgsqlCommand("""
            SELECT installation_id FROM cloud_license_installations WHERE installation_id = @installation_id FOR UPDATE
            """, connection, transaction);
        lockInstallation.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
        if (await lockInstallation.ExecuteScalarAsync(cancellationToken) is not Guid)
            return AssignInstallationResult.InstallationNotFound;

        await using var countCommand = new NpgsqlCommand("""
            SELECT count(*) FROM cloud_license_installations
            WHERE license_id = @license_id AND is_assigned = true AND installation_id <> @installation_id
            """, connection, transaction);
        countCommand.Parameters.AddWithValue("license_id", NpgsqlDbType.Uuid, licenseId);
        countCommand.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
        var assignedCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        if (assignedCount >= maxInstallations) return AssignInstallationResult.InstallationLimitReached;

        await using var assign = new NpgsqlCommand("""
            UPDATE cloud_license_installations
            SET license_id = @license_id, is_assigned = true
            WHERE installation_id = @installation_id
            """, connection, transaction);
        assign.Parameters.AddWithValue("license_id", NpgsqlDbType.Uuid, licenseId);
        assign.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
        await assign.ExecuteNonQueryAsync(cancellationToken);
        await AddAuditAsync(connection, transaction, licenseId, installationId, "assigned", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AssignInstallationResult.Success;
    }

    public async Task<bool> DetachInstallationAsync(Guid installationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid? licenseId;
        await using (var update = new NpgsqlCommand("""
            UPDATE cloud_license_installations SET is_assigned = false
            WHERE installation_id = @installation_id AND is_assigned = true
            RETURNING license_id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
            var value = await update.ExecuteScalarAsync(cancellationToken);
            if (value is not Guid id) return false;
            licenseId = id;
        }
        await AddAuditAsync(connection, transaction, licenseId, installationId, "detached", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<LicenseManagerDashboard> GetDashboardAsync(CancellationToken cancellationToken)
    {
        var customers = new List<ManagedCustomerView>();
        var licenses = new List<ManagedLicenseView>();
        var installations = new List<ManagedInstallationView>();
        var editions = new List<ManagedLicenseEditionView>();
        var categories = new List<ManagedLicenseManagerCategoryView>();
        var audit = new List<ManagedCustomerAuditView>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            SELECT c.id, c.store_identifier, c.shop_name, c.reseller_name, c.vat_number,
                   c.is_active, c.created_at_utc,
                   count(DISTINCT l.id) FILTER (WHERE c.is_active AND l.status = 'active' AND (l.expires_at_utc IS NULL OR l.expires_at_utc > now())),
                   count(DISTINCT i.installation_id) FILTER (WHERE c.is_active AND i.is_assigned AND l.status = 'active' AND (l.expires_at_utc IS NULL OR l.expires_at_utc > now())),
                   c.category_id, category.name
            FROM cloud_license_customers c
            LEFT JOIN cloud_license_categories category ON category.id = c.category_id
            LEFT JOIN cloud_licenses l ON l.customer_id = c.id
            LEFT JOIN cloud_license_installations i ON i.license_id = l.id
            GROUP BY c.id, category.id
            ORDER BY c.is_active DESC, c.shop_name, c.created_at_utc DESC
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                customers.Add(new ManagedCustomerView(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetBoolean(5),
                    Convert.ToInt32(reader.GetInt64(7)), Convert.ToInt32(reader.GetInt64(8)), reader.GetDateTime(6),
                    reader.IsDBNull(9) ? null : reader.GetGuid(9), reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        await using (var command = new NpgsqlCommand("""
            SELECT l.id, l.customer_id, c.store_identifier, c.shop_name, c.reseller_name,
                   c.vat_number, l.edition_id, e.name, l.status, l.expires_at_utc,
                   l.max_installations, count(i.installation_id) FILTER (WHERE i.is_assigned), l.created_at_utc
            FROM cloud_licenses l
            JOIN cloud_license_customers c ON c.id = l.customer_id
            LEFT JOIN cloud_license_editions e ON e.id = l.edition_id
            LEFT JOIN cloud_license_installations i ON i.license_id = l.id
            GROUP BY l.id, c.id, e.id
            ORDER BY c.shop_name, l.created_at_utc DESC
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                licenses.Add(new ManagedLicenseView(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetDateTime(9), reader.GetInt32(10),
                    Convert.ToInt32(reader.GetInt64(11)), reader.GetDateTime(12)));
        }

        await using (var command = new NpgsqlCommand("""
            SELECT e.id, e.name, e.max_installations, e.default_duration_months, e.is_active,
                   count(l.id)
            FROM cloud_license_editions e
            LEFT JOIN cloud_licenses l ON l.edition_id = e.id
            GROUP BY e.id
            ORDER BY e.is_active DESC, e.name
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                editions.Add(new ManagedLicenseEditionView(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2),
                    reader.GetInt32(3), reader.GetBoolean(4), Convert.ToInt32(reader.GetInt64(5))));
        }

        await using (var command = new NpgsqlCommand("""
            SELECT category.id, category.name,
                   count(customer.id),
                   category.created_at_utc
            FROM cloud_license_categories category
            LEFT JOIN cloud_license_customers customer ON customer.category_id = category.id
            GROUP BY category.id
            ORDER BY lower(category.name)
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                categories.Add(new ManagedLicenseManagerCategoryView(reader.GetGuid(0), reader.GetString(1),
                    Convert.ToInt32(reader.GetInt64(2)), reader.GetDateTime(3)));
        }

        await using (var command = new NpgsqlCommand("""
            SELECT id, customer_id, action, created_at_utc, license_id, installation_id
            FROM cloud_license_audit
            WHERE customer_id IS NOT NULL
            ORDER BY created_at_utc DESC
            LIMIT 1000
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                audit.Add(new ManagedCustomerAuditView(reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2),
                    reader.GetDateTime(3), reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    reader.IsDBNull(5) ? null : reader.GetGuid(5)));
        }

        await using (var command = new NpgsqlCommand("""
            SELECT i.installation_id, i.machine_name, i.first_seen_at_utc, i.last_seen_at_utc,
                   i.license_id, c.shop_name, c.store_identifier,
                   CASE WHEN c.is_active THEN l.status ELSE 'revoked' END,
                   i.is_assigned, i.rebind_pending
            FROM cloud_license_installations i
            LEFT JOIN cloud_licenses l ON l.id = i.license_id
            LEFT JOIN cloud_license_customers c ON c.id = l.customer_id
            ORDER BY i.is_assigned ASC, i.last_seen_at_utc DESC
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                installations.Add(new ManagedInstallationView(reader.GetGuid(0), reader.GetString(1),
                    reader.GetDateTime(2), reader.GetDateTime(3), reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetBoolean(8), reader.GetBoolean(9)));
        }

        return new LicenseManagerDashboard(customers, licenses, installations, editions, categories, audit);
    }

    private async Task AddAuditAsync(Guid? licenseId, Guid? installationId, string action,
        CancellationToken cancellationToken, Guid? customerId = null)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await AddAuditAsync(connection, null, licenseId, installationId, action, cancellationToken, customerId);
    }

    private static async Task AddAuditAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid? licenseId, Guid? installationId, string action, CancellationToken cancellationToken,
        Guid? customerId = null)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO cloud_license_audit (license_id, installation_id, customer_id, action)
            VALUES (
                @license_id,
                @installation_id,
                COALESCE(
                    @customer_id,
                    (SELECT customer_id FROM cloud_licenses WHERE id = @license_id),
                    (SELECT license.customer_id
                     FROM cloud_license_installations installation
                     JOIN cloud_licenses license ON license.id = installation.license_id
                     WHERE installation.installation_id = @installation_id)
                ),
                @action
            )
            """, connection, transaction);
        command.Parameters.AddWithValue("license_id", NpgsqlDbType.Uuid, licenseId is null ? DBNull.Value : licenseId.Value);
        command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId is null ? DBNull.Value : installationId.Value);
        command.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId is null ? DBNull.Value : customerId.Value);
        command.Parameters.AddWithValue("action", NpgsqlDbType.Varchar, action);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddTimestamp(NpgsqlCommand command, string name, DateTime? value) =>
        command.Parameters.AddWithValue(name, NpgsqlDbType.TimestampTz, value is null ? DBNull.Value : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private static byte[] HashMac(string macAddress)
    {
        var normalized = new string(macAddress.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        return SHA256.HashData(Encoding.ASCII.GetBytes(normalized));
    }
}

public sealed record LicenseActivationInfo(Guid? LicenseId, bool IsAssigned, string CustomerName,
    string VatNumber, string Status, DateTime? ExpiresAtUtc);

public enum AssignInstallationResult
{
    Success,
    LicenseNotFound,
    LicenseInactive,
    InstallationNotFound,
    InstallationLimitReached
}
