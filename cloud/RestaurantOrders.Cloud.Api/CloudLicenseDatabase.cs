using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace RestaurantOrders.Cloud.Api;

public sealed class CloudLicenseDatabase(NpgsqlDataSource dataSource)
{
    private const string PendingMachineName = "Pending activation";

    public async Task<bool> RegisterInstallationAsync(
        Guid installationId, string machineName, string macAddress, CancellationToken cancellationToken)
    {
        var macHash = HashMac(macAddress);
        var pendingMacHash = HashMac("");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO cloud_license_installations (installation_id, machine_name, mac_address_sha256)
            VALUES (@installation_id, @machine_name, @mac_hash)
            ON CONFLICT (installation_id) DO UPDATE
            SET machine_name = CASE
                    WHEN cloud_license_installations.machine_name = @pending_machine_name
                     AND cloud_license_installations.mac_address_sha256 = @pending_mac_hash
                    THEN EXCLUDED.machine_name ELSE cloud_license_installations.machine_name END,
                mac_address_sha256 = CASE
                    WHEN cloud_license_installations.machine_name = @pending_machine_name
                     AND cloud_license_installations.mac_address_sha256 = @pending_mac_hash
                    THEN EXCLUDED.mac_address_sha256 ELSE cloud_license_installations.mac_address_sha256 END,
                last_seen_at_utc = now()
            WHERE (cloud_license_installations.machine_name = @pending_machine_name
                   AND cloud_license_installations.mac_address_sha256 = @pending_mac_hash)
               OR (cloud_license_installations.machine_name = EXCLUDED.machine_name
                   AND cloud_license_installations.mac_address_sha256 = EXCLUDED.mac_address_sha256)
            RETURNING installation_id
            """, connection);
        command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId);
        command.Parameters.AddWithValue("machine_name", NpgsqlDbType.Varchar, machineName);
        command.Parameters.AddWithValue("mac_hash", NpgsqlDbType.Bytea, macHash);
        command.Parameters.AddWithValue("pending_machine_name", NpgsqlDbType.Varchar, PendingMachineName);
        command.Parameters.AddWithValue("pending_mac_hash", NpgsqlDbType.Bytea, pendingMacHash);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid;
    }

    public async Task<LicenseActivationInfo?> GetActivationInfoAsync(Guid installationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT i.license_id, i.is_assigned,
                   l.customer_name, l.vat_number, l.status, l.expires_at_utc
            FROM cloud_license_installations i
            LEFT JOIN cloud_licenses l ON l.id = i.license_id
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

    public async Task<CreateLicenseResult> CreateLicenseAsync(
        string customerName, string vatNumber, DateTime? expiresAtUtc, int maxInstallations,
        string comments, Guid activationCode, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var addPendingInstallation = new NpgsqlCommand("""
            INSERT INTO cloud_license_installations (installation_id, machine_name, mac_address_sha256)
            VALUES (@installation_id, @machine_name, @mac_hash)
            ON CONFLICT (installation_id) DO NOTHING
            """, connection, transaction))
        {
            addPendingInstallation.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, activationCode);
            addPendingInstallation.Parameters.AddWithValue("machine_name", NpgsqlDbType.Varchar, PendingMachineName);
            addPendingInstallation.Parameters.AddWithValue("mac_hash", NpgsqlDbType.Bytea, HashMac(""));
            await addPendingInstallation.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var lockInstallation = new NpgsqlCommand("""
            SELECT is_assigned FROM cloud_license_installations
            WHERE installation_id = @installation_id FOR UPDATE
            """, connection, transaction))
        {
            lockInstallation.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, activationCode);
            if (await lockInstallation.ExecuteScalarAsync(cancellationToken) is not false)
                return new(null, true);
        }

        await using (var insertLicense = new NpgsqlCommand("""
            INSERT INTO cloud_licenses (id, customer_name, vat_number, expires_at_utc,
                max_installations, comments)
            VALUES (@id, @customer_name, @vat_number, @expires_at_utc,
                @max_installations, @comments)
            """, connection, transaction))
        {
            insertLicense.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
            insertLicense.Parameters.AddWithValue("customer_name", NpgsqlDbType.Varchar, customerName);
            insertLicense.Parameters.AddWithValue("vat_number", NpgsqlDbType.Varchar, vatNumber);
            AddTimestamp(insertLicense, "expires_at_utc", expiresAtUtc);
            insertLicense.Parameters.AddWithValue("max_installations", NpgsqlDbType.Integer, maxInstallations);
            insertLicense.Parameters.AddWithValue("comments", NpgsqlDbType.Text, comments);
            await insertLicense.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var assign = new NpgsqlCommand("""
            UPDATE cloud_license_installations
            SET license_id = @license_id, is_assigned = true
            WHERE installation_id = @installation_id
            """, connection, transaction))
        {
            assign.Parameters.AddWithValue("license_id", NpgsqlDbType.Uuid, id);
            assign.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, activationCode);
            await assign.ExecuteNonQueryAsync(cancellationToken);
        }

        await AddAuditAsync(connection, transaction, id, activationCode, "created-and-activated", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(id, false);
    }

    public async Task<bool> UpdateLicenseAsync(Guid licenseId, string customerName, string vatNumber,
        DateTime? expiresAtUtc, int maxInstallations, string comments, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE cloud_licenses
            SET customer_name = @customer_name, vat_number = @vat_number,
                expires_at_utc = @expires_at_utc, max_installations = @max_installations,
                comments = @comments, updated_at_utc = now()
            WHERE id = @id AND status <> 'deleted'
              AND @max_installations >= (
                  SELECT count(*) FROM cloud_license_installations
                  WHERE license_id = @id AND is_assigned = true)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, licenseId);
        command.Parameters.AddWithValue("customer_name", NpgsqlDbType.Varchar, customerName);
        command.Parameters.AddWithValue("vat_number", NpgsqlDbType.Varchar, vatNumber);
        AddTimestamp(command, "expires_at_utc", expiresAtUtc);
        command.Parameters.AddWithValue("max_installations", NpgsqlDbType.Integer, maxInstallations);
        command.Parameters.AddWithValue("comments", NpgsqlDbType.Text, comments);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await AddAuditAsync(connection, transaction, licenseId, null, "updated", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RenewLicenseAsync(Guid licenseId, int durationMonths, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        DateTime? currentExpiry;
        await using (var readExpiry = new NpgsqlCommand("""
            SELECT expires_at_utc FROM cloud_licenses
            WHERE id = @id AND status <> 'deleted' FOR UPDATE
            """, connection, transaction))
        {
            readExpiry.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, licenseId);
            var value = await readExpiry.ExecuteScalarAsync(cancellationToken);
            if (value is null) return false;
            currentExpiry = value is DateTime date ? date : null;
        }

        var now = DateTime.UtcNow;
        var expiresAtUtc = currentExpiry is null
            ? null
            : (currentExpiry.Value > now ? currentExpiry.Value : now).AddMonths(durationMonths);
        await using var command = new NpgsqlCommand("""
            UPDATE cloud_licenses
            SET expires_at_utc = @expires_at_utc, status = 'active', updated_at_utc = now()
            WHERE id = @id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, licenseId);
        AddTimestamp(command, "expires_at_utc", expiresAtUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await AddAuditAsync(connection, transaction, licenseId, null, "renewed", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteLicenseAsync(Guid licenseId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("""
            UPDATE cloud_licenses SET status = 'deleted', updated_at_utc = now()
            WHERE id = @id AND status <> 'deleted'
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, licenseId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        }

        await using (var detach = new NpgsqlCommand("""
            UPDATE cloud_license_installations SET is_assigned = false
            WHERE license_id = @id AND is_assigned = true
            """, connection, transaction))
        {
            detach.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, licenseId);
            await detach.ExecuteNonQueryAsync(cancellationToken);
        }

        await AddAuditAsync(connection, transaction, licenseId, null, "deleted", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RevokeLicenseAsync(Guid licenseId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE cloud_licenses SET status = 'revoked', updated_at_utc = now()
            WHERE id = @id AND status <> 'deleted'
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
            SELECT max_installations, status, expires_at_utc
            FROM cloud_licenses WHERE id = @license_id FOR UPDATE
            """, connection, transaction);
        lockLicense.Parameters.AddWithValue("license_id", NpgsqlDbType.Uuid, licenseId);
        int maxInstallations;
        await using (var reader = await lockLicense.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return AssignInstallationResult.LicenseNotFound;
            maxInstallations = reader.GetInt32(0);
            var status = reader.GetString(1);
            DateTime? expiresAtUtc = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
            if (status != "active" || (expiresAtUtc is not null && expiresAtUtc <= DateTime.UtcNow))
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

    public async Task<(IReadOnlyList<ManagedLicenseView> Licenses, IReadOnlyList<ManagedInstallationView> Installations)> GetDashboardAsync(
        CancellationToken cancellationToken)
    {
        var licenses = new List<ManagedLicenseView>();
        var installations = new List<ManagedInstallationView>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            SELECT l.id, l.customer_name, l.vat_number, l.status, l.expires_at_utc,
                   l.max_installations, count(i.installation_id) FILTER (WHERE i.is_assigned),
                   l.created_at_utc, l.comments
            FROM cloud_licenses l
            LEFT JOIN cloud_license_installations i ON i.license_id = l.id
            WHERE l.status <> 'deleted'
            GROUP BY l.id
            ORDER BY l.created_at_utc DESC
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                licenses.Add(new ManagedLicenseView(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetDateTime(4), reader.GetInt32(5),
                    Convert.ToInt32(reader.GetInt64(6)), reader.GetDateTime(7), reader.GetString(8)));
        }

        await using (var command = new NpgsqlCommand("""
            SELECT i.installation_id, i.machine_name, i.first_seen_at_utc, i.last_seen_at_utc,
                   i.license_id, l.customer_name, l.status, i.is_assigned
            FROM cloud_license_installations i
            LEFT JOIN cloud_licenses l ON l.id = i.license_id
            ORDER BY i.is_assigned ASC, i.last_seen_at_utc DESC
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                installations.Add(new ManagedInstallationView(reader.GetGuid(0), reader.GetString(1),
                    reader.GetDateTime(2), reader.GetDateTime(3), reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetBoolean(7)));
        }

        return (licenses, installations);
    }

    private async Task AddAuditAsync(Guid? licenseId, Guid? installationId, string action, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await AddAuditAsync(connection, null, licenseId, installationId, action, cancellationToken);
    }

    private static async Task AddAuditAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid? licenseId, Guid? installationId, string action, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO cloud_license_audit (license_id, installation_id, action)
            VALUES (@license_id, @installation_id, @action)
            """, connection, transaction);
        command.Parameters.AddWithValue("license_id", NpgsqlDbType.Uuid, licenseId is null ? DBNull.Value : licenseId.Value);
        command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Uuid, installationId is null ? DBNull.Value : installationId.Value);
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
