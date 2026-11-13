using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using RestaurantOrders.Cloud.Api;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 5 * 1024 * 1024);

var connectionString = builder.Configuration.GetConnectionString("Cloud");
if (string.IsNullOrWhiteSpace(connectionString))
{
    var databasePassword = ReadConfiguredSecret(builder.Configuration, "Cloud:DatabasePassword", "Cloud:DatabasePasswordFile");
    if (Encoding.UTF8.GetByteCount(databasePassword) < 32)
        throw new InvalidOperationException("Cloud database password must contain at least 32 bytes of secret material.");

    var database = new NpgsqlConnectionStringBuilder
    {
        Host = builder.Configuration["Cloud:DatabaseHost"] ?? "postgres",
        Port = builder.Configuration.GetValue("Cloud:DatabasePort", 5432),
        Database = builder.Configuration["Cloud:DatabaseName"] ?? "restaurant_cloud",
        Username = builder.Configuration["Cloud:DatabaseUser"] ?? "restaurant_cloud",
        Password = databasePassword,
        Timeout = 15,
        CommandTimeout = 30,
        Pooling = true
    };
    connectionString = database.ConnectionString;
}

var adminKey = ReadConfiguredSecret(builder.Configuration, "Cloud:AdminKey", "Cloud:AdminKeyFile");
if (string.IsNullOrWhiteSpace(adminKey) || Encoding.UTF8.GetByteCount(adminKey) < 32)
    throw new InvalidOperationException("Cloud:AdminKey must contain at least 32 bytes of secret material.");
var licenseManagerPassword = ReadConfiguredSecret(builder.Configuration, "Cloud:LicenseManagerPassword", "Cloud:LicenseManagerPasswordFile");
if (Encoding.UTF8.GetByteCount(licenseManagerPassword) < 32)
    throw new InvalidOperationException("Cloud:LicenseManagerPassword must contain at least 32 bytes of secret material.");
var licenseSigningPrivateKeyPath = builder.Configuration["Cloud:LicenseSigningPrivateKeyFile"];
if (string.IsNullOrWhiteSpace(licenseSigningPrivateKeyPath) || !File.Exists(licenseSigningPrivateKeyPath))
    throw new InvalidOperationException("Cloud:LicenseSigningPrivateKeyFile must point to the RSA private key secret file.");
var licenseSigningPrivateKey = await File.ReadAllTextAsync(licenseSigningPrivateKeyPath);

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<CloudDatabase>();
builder.Services.AddSingleton<CloudLicenseDatabase>();
builder.Services.AddSingleton(new LicenseSigner(licenseSigningPrivateKey));

var app = builder.Build();
app.UseStaticFiles();

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<CloudDatabase>();
    await database.ApplyMigrationsAsync(CancellationToken.None);
}

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (CloudDatabase database, CancellationToken cancellationToken) =>
{
    var ready = await database.CanConnectAsync(cancellationToken);
    return ready ? Results.Ok(new { status = "ready" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapPost("/api/admin/v1/stores", async (
    HttpRequest request,
    CloudDatabase database,
    CancellationToken cancellationToken) =>
{
    var suppliedAdminKey = request.Headers["X-Cloud-Admin-Key"].ToString();
    if (!HasMatchingSecret(suppliedAdminKey, adminKey))
        return Results.Unauthorized();

    CreateStoreRequest? body;
    try
    {
        body = await request.ReadFromJsonAsync<CreateStoreRequest>(cancellationToken);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Το σώμα του αιτήματος δεν είναι έγκυρο JSON." });
    }

    var name = body?.Name?.Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
        return Results.BadRequest(new { error = "Το όνομα καταστήματος πρέπει να έχει 1 έως 120 χαρακτήρες." });

    var storeId = Guid.NewGuid();
    var storeToken = StoreTokens.Create();
    await database.CreateStoreAsync(storeId, name, StoreTokens.Hash(storeToken), cancellationToken);

    return Results.Created($"/api/admin/v1/stores/{storeId}", new
    {
        storeId,
        name,
        apiToken = storeToken,
        message = "Αποθήκευσε το apiToken στο συγκεκριμένο κατάστημα. Δεν θα εμφανιστεί ξανά."
    });
});

app.MapPost("/api/admin/v1/stores/{storeId:guid}/rotate-token", async (
    Guid storeId,
    HttpRequest request,
    CloudDatabase database,
    CancellationToken cancellationToken) =>
{
    var suppliedAdminKey = request.Headers["X-Cloud-Admin-Key"].ToString();
    if (!HasMatchingSecret(suppliedAdminKey, adminKey))
        return Results.Unauthorized();

    var storeToken = StoreTokens.Create();
    var updated = await database.RotateStoreTokenAsync(storeId, StoreTokens.Hash(storeToken), cancellationToken);
    if (!updated)
        return Results.NotFound(new { error = "Δεν βρέθηκε ενεργό κατάστημα με αυτό το StoreId." });

    return Results.Ok(new
    {
        storeId,
        apiToken = storeToken,
        message = "Το προηγούμενο apiToken ακυρώθηκε. Αποθήκευσε το νέο token στο κατάστημα."
    });
});

app.MapPost("/api/license/v1/check", async (
    RemoteLicenseCheckRequest? body,
    CloudLicenseDatabase database,
    LicenseSigner signer,
    CancellationToken cancellationToken) =>
{
    if (body is null || !Guid.TryParse(body.InstallationId, out var installationId) ||
        string.IsNullOrWhiteSpace(body.MachineName) || body.MachineName.Length > 120 ||
        body.MacAddress is null || body.MacAddress.Length > 64 ||
        body.MacAddress.Any(c => !Uri.IsHexDigit(c) && c is not ':' and not '-' and not ' '))
        return Results.BadRequest(new { error = "Τα στοιχεία εγκατάστασης δεν είναι έγκυρα." });

    var machineName = body.MachineName.Trim();
    if (!await database.RegisterInstallationAsync(installationId, machineName, body.MacAddress, cancellationToken))
        return Results.Conflict(new { error = "Το αναγνωριστικό εγκατάστασης έχει ήδη δεθεί με διαφορετικό υπολογιστή. Επικοινώνησε με τον προμηθευτή." });

    var activation = await database.GetActivationInfoAsync(installationId, cancellationToken);
    if (activation is null)
        return Results.NotFound(new { error = "Δεν βρέθηκε η εγκατάσταση." });
    if (activation.LicenseId is null)
        return Results.Ok(new RemoteLicenseCheckResponse
        {
            Status = "pending",
            Message = "Η εγκατάσταση καταχωρίστηκε και περιμένει ενεργοποίηση από τον License Manager."
        });

    var now = DateTime.UtcNow;
    var isActive = activation.IsAssigned && activation.Status == "active" &&
                   (activation.ExpiresAtUtc is null || activation.ExpiresAtUtc.Value > now);
    var status = isActive ? "active" : "revoked";
    var leaseExpiry = isActive
        ? (activation.ExpiresAtUtc is null || activation.ExpiresAtUtc.Value > now.AddDays(30)
            ? now.AddDays(30)
            : activation.ExpiresAtUtc.Value)
        : now;
    var payload = new CloudLicensePayload
    {
        LicenseId = activation.LicenseId.Value.ToString("D"),
        CustomerName = activation.CustomerName,
        VatNumber = activation.VatNumber,
        InstallationId = installationId.ToString("D"),
        MachineName = machineName,
        MacAddress = body.MacAddress,
        IssuedAtUtc = now,
        ExpiresAtUtc = activation.ExpiresAtUtc,
        RemoteStatus = status,
        LeaseExpiresAtUtc = leaseExpiry
    };

    return Results.Ok(new RemoteLicenseCheckResponse
    {
        Status = status,
        License = signer.Sign(payload),
        Message = isActive ? "Η άδεια χρήσης είναι ενεργή." : "Η άδεια ανακλήθηκε ή έληξε."
    });
});

var licenseManager = app.MapGroup("/api/license-manager/v1");
licenseManager.AddEndpointFilter(async (context, next) =>
{
    if (!HasLicenseManagerAccess(context.HttpContext.Request, licenseManagerPassword))
        return Results.Unauthorized();
    return await next(context);
});

licenseManager.MapGet("/dashboard", async (CloudLicenseDatabase database, CancellationToken cancellationToken) =>
{
    var dashboard = await database.GetDashboardAsync(cancellationToken);
    return Results.Ok(new { licenses = dashboard.Licenses, installations = dashboard.Installations });
});

licenseManager.MapPost("/licenses", async (
    CreateCloudLicenseRequest? body,
    CloudLicenseDatabase database,
    CancellationToken cancellationToken) =>
{
    var customerName = body?.CustomerName?.Trim();
    var vatNumber = body?.VatNumber?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(customerName) || customerName.Length > 160 || vatNumber.Length > 32)
        return Results.BadRequest(new { error = "Συμπλήρωσε επωνυμία έως 160 χαρακτήρες και ΑΦΜ έως 32 χαρακτήρες." });
    if (body!.MaxInstallations is < 1 or > 100)
        return Results.BadRequest(new { error = "Οι επιτρεπόμενες εγκαταστάσεις πρέπει να είναι από 1 έως 100." });
    var expiresAtUtc = body.ExpiresAtUtc?.UtcDateTime;
    if (expiresAtUtc is not null && expiresAtUtc <= DateTime.UtcNow)
        return Results.BadRequest(new { error = "Η ημερομηνία λήξης πρέπει να είναι στο μέλλον." });

    var licenseId = await database.CreateLicenseAsync(customerName, vatNumber, expiresAtUtc,
        body.MaxInstallations, cancellationToken);
    return Results.Created($"/api/license-manager/v1/licenses/{licenseId}", new { licenseId });
});

licenseManager.MapPut("/licenses/{licenseId:guid}/renew", async (
    Guid licenseId,
    RenewCloudLicenseRequest? body,
    CloudLicenseDatabase database,
    CancellationToken cancellationToken) =>
{
    var expiresAtUtc = body?.ExpiresAtUtc?.UtcDateTime;
    if (expiresAtUtc is not null && expiresAtUtc <= DateTime.UtcNow)
        return Results.BadRequest(new { error = "Η ημερομηνία λήξης πρέπει να είναι στο μέλλον." });
    var renewed = await database.RenewLicenseAsync(licenseId, expiresAtUtc, cancellationToken);
    return renewed ? Results.Ok(new { status = "active" }) : Results.NotFound(new { error = "Δεν βρέθηκε η άδεια." });
});

licenseManager.MapPost("/licenses/{licenseId:guid}/revoke", async (
    Guid licenseId,
    CloudLicenseDatabase database,
    CancellationToken cancellationToken) =>
{
    var revoked = await database.RevokeLicenseAsync(licenseId, cancellationToken);
    return revoked ? Results.Ok(new { status = "revoked" }) : Results.NotFound(new { error = "Δεν βρέθηκε η άδεια." });
});

licenseManager.MapPost("/installations/{installationId:guid}/assign", async (
    Guid installationId,
    AssignCloudLicenseRequest? body,
    CloudLicenseDatabase database,
    CancellationToken cancellationToken) =>
{
    if (body is null || body.LicenseId == Guid.Empty)
        return Results.BadRequest(new { error = "Επίλεξε έγκυρη άδεια." });
    var result = await database.AssignInstallationAsync(installationId, body.LicenseId, cancellationToken);
    return result switch
    {
        AssignInstallationResult.Success => Results.Ok(new { status = "assigned" }),
        AssignInstallationResult.LicenseNotFound => Results.NotFound(new { error = "Δεν βρέθηκε η άδεια." }),
        AssignInstallationResult.InstallationNotFound => Results.NotFound(new { error = "Δεν βρέθηκε η εγκατάσταση." }),
        AssignInstallationResult.LicenseInactive => Results.Conflict(new { error = "Η άδεια είναι ακυρωμένη ή έχει λήξει." }),
        _ => Results.Conflict(new { error = "Η άδεια έχει φτάσει το όριο ενεργών εγκαταστάσεων." })
    };
});

licenseManager.MapPost("/installations/{installationId:guid}/detach", async (
    Guid installationId,
    CloudLicenseDatabase database,
    CancellationToken cancellationToken) =>
{
    var detached = await database.DetachInstallationAsync(installationId, cancellationToken);
    return detached ? Results.Ok(new { status = "detached" }) : Results.NotFound(new { error = "Η εγκατάσταση δεν έχει ενεργή άδεια." });
});

app.MapGet("/license-manager", () => Results.Redirect("/license-manager.html"));

app.MapPost("/api/sync/v1/events", async (
    HttpRequest request,
    CloudDatabase database,
    CancellationToken cancellationToken) =>
{
    var token = ReadBearerToken(request.Headers.Authorization.ToString());
    if (token is null)
        return Results.Unauthorized();

    var storeId = await database.FindActiveStoreIdAsync(StoreTokens.Hash(token), cancellationToken);
    if (storeId is null)
        return Results.Unauthorized();

    SyncBatchRequest? body;
    try
    {
        body = await request.ReadFromJsonAsync<SyncBatchRequest>(cancellationToken);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Το σώμα του αιτήματος δεν είναι έγκυρο JSON." });
    }

    var events = body?.Events;
    if (events is null || events.Count is < 1 or > 100)
        return Results.BadRequest(new { error = "Κάθε αποστολή πρέπει να περιέχει από 1 έως 100 συμβάντα." });

    var validationError = ValidateEvents(events);
    if (validationError is not null)
        return Results.BadRequest(new { error = validationError });

    try
    {
        var (accepted, duplicates) = await database.InsertEventsAsync(storeId.Value, events, cancellationToken);
        return Results.Ok(new SyncBatchResponse(storeId.Value, accepted, duplicates));
    }
    catch (SyncEventConflictException)
    {
        return Results.Conflict(new { error = "Ένα EventId έχει ήδη χρησιμοποιηθεί με διαφορετικά δεδομένα." });
    }
});

app.Run();

static bool HasMatchingSecret(string supplied, string expected)
{
    var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
    var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
    return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
}

static bool HasLicenseManagerAccess(HttpRequest request, string expectedPassword)
{
    const string prefix = "Basic ";
    var authorization = request.Headers.Authorization.ToString();
    if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
    try
    {
        var credential = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[prefix.Length..].Trim()));
        var separator = credential.IndexOf(':');
        if (separator < 0 || !string.Equals(credential[..separator], "manager", StringComparison.Ordinal)) return false;
        return HasMatchingSecret(credential[(separator + 1)..], expectedPassword);
    }
    catch (FormatException)
    {
        return false;
    }
}

static string? ReadBearerToken(string authorization)
{
    const string prefix = "Bearer ";
    if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        return null;

    var token = authorization[prefix.Length..].Trim();
    return token.Length is >= 32 and <= 256 ? token : null;
}

static string? ValidateEvents(IReadOnlyList<SyncEventRequest?> events)
{
    var ids = new HashSet<Guid>();
    foreach (var item in events)
    {
        if (item is null)
            return "Η λίστα περιέχει κενό συμβάν.";
        if (item.EventId == Guid.Empty || !ids.Add(item.EventId))
            return "Κάθε συμβάν χρειάζεται μοναδικό, μη κενό EventId.";
        if (!MatchesName(item.EntityType, 80) || !MatchesName(item.EventType, 40))
            return "Το EntityType ή το EventType έχει μη έγκυρη μορφή.";
        if (string.IsNullOrWhiteSpace(item.EntityId) || item.EntityId.Length > 160)
            return "Το EntityId πρέπει να έχει 1 έως 160 χαρακτήρες.";
        if (item.SchemaVersion is < 1 or > 1000)
            return "Το SchemaVersion πρέπει να είναι από 1 έως 1000.";
        if (item.OccurredAtUtc == default)
            return "Το OccurredAtUtc είναι υποχρεωτικό.";
        if (item.OccurredAtUtc.Offset != TimeSpan.Zero)
            return "Το OccurredAtUtc πρέπει να αποστέλλεται σε UTC.";
        if (item.Payload.ValueKind != JsonValueKind.Object)
            return "Το Payload κάθε συμβάντος πρέπει να είναι αντικείμενο JSON.";
        if (JsonSerializer.SerializeToUtf8Bytes(item.Payload).Length > 256 * 1024)
            return "Το Payload ενός συμβάντος δεν μπορεί να ξεπερνά τα 256 KB.";
    }

    return null;
}

static bool MatchesName(string? value, int maxLength) =>
    !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength &&
    Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);

static string ReadConfiguredSecret(IConfiguration configuration, string directKey, string fileKey)
{
    var secretPath = configuration[fileKey];
    var value = !string.IsNullOrWhiteSpace(secretPath)
        ? File.ReadAllText(secretPath).TrimEnd('\r', '\n')
        : configuration[directKey];
    return string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"{directKey} or {fileKey} must be configured.")
        : value;
}

