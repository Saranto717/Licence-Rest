using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
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
var licenseManagerEmail = builder.Configuration["Cloud:LicenseManagerEmail"]?.Trim();
if (!IsValidEmailAddress(licenseManagerEmail))
    throw new InvalidOperationException("Cloud:LicenseManagerEmail must contain a valid email address.");
var licenseManagerPassword = ReadConfiguredSecret(builder.Configuration, "Cloud:LicenseManagerPassword", "Cloud:LicenseManagerPasswordFile");
if (licenseManagerPassword.Length < 12)
    throw new InvalidOperationException("Cloud:LicenseManagerPassword must contain at least 12 characters.");
var licenseSigningPrivateKey = await ReadLicenseSigningPrivateKeyAsync(builder.Configuration);

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<CloudDatabase>();
builder.Services.AddSingleton<CloudLicenseDatabase>();
builder.Services.AddSingleton<LicenseManagerSessions>();
builder.Services.AddSingleton(new LicenseSigner(licenseSigningPrivateKey));

var app = builder.Build();
app.MapGet("/", () => Results.Redirect("/license-manager.html"));
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (string.Equals(context.File.Name, "license-manager.html", StringComparison.OrdinalIgnoreCase))
            context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    }
});

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
    HttpContext context,
    CloudLicenseDatabase database,
    LicenseSigner signer,
    CancellationToken cancellationToken) =>
{
    var request = context.Request;
    const int maxBodySize = 16 * 1024;
    if (request.ContentLength is < 1 or > maxBodySize)
        return Results.BadRequest(new { error = "Το αίτημα άδειας έχει μη έγκυρο μέγεθος." });

    using var bodyBuffer = new MemoryStream((int)(request.ContentLength ?? 512));
    var readBuffer = new byte[4096];
    while (true)
    {
        var read = await request.Body.ReadAsync(readBuffer.AsMemory(), cancellationToken);
        if (read == 0) break;
        if (bodyBuffer.Length + read > maxBodySize)
            return Results.BadRequest(new { error = "Το αίτημα άδειας υπερβαίνει το επιτρεπτό μέγεθος." });
        await bodyBuffer.WriteAsync(readBuffer.AsMemory(0, read), cancellationToken);
    }
    if (bodyBuffer.Length == 0)
        return Results.BadRequest(new { error = "Το αίτημα άδειας είναι κενό." });
    var bodyBytes = bodyBuffer.ToArray();

    RemoteLicenseCheckRequest? body;
    try
    {
        body = JsonSerializer.Deserialize<RemoteLicenseCheckRequest>(bodyBytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "Το σώμα του αιτήματος δεν είναι έγκυρο JSON." });
    }

    if (body is null || !Guid.TryParse(body.InstallationId, out var installationId) ||
        string.IsNullOrWhiteSpace(body.MachineName) || body.MachineName.Length > 120 ||
        body.MacAddress is null || body.MacAddress.Length > 64 ||
        body.MacAddress.Any(c => !Uri.IsHexDigit(c) && c is not ':' and not '-' and not ' '))
        return Results.BadRequest(new { error = "Τα στοιχεία εγκατάστασης δεν είναι έγκυρα." });

    var machineName = body.MachineName.Trim();
    var hasSignatureHeaders = request.Headers.ContainsKey("X-License-Public-Key") ||
                              request.Headers.ContainsKey("X-License-Timestamp") ||
                              request.Headers.ContainsKey("X-License-Nonce") ||
                              request.Headers.ContainsKey("X-License-Signature");
    bool registered;
    if (hasSignatureHeaders)
    {
        if (!TryVerifyLicenseCheckSignature(request, bodyBytes, installationId,
                out var clientPublicKey, out var nonceHash))
            return Results.Unauthorized();

        registered = await database.RegisterSignedInstallationRequestAsync(installationId, machineName,
            body.MacAddress, clientPublicKey, nonceHash, cancellationToken);
        if (registered)
            context.Response.Headers["X-License-Auth"] = "signature-v1";
    }
    else
    {
        // Temporary compatibility for already-installed clients that still use the previous bearer protocol.
        var clientToken = ReadBearerToken(request.Headers.Authorization.ToString());
        if (clientToken is null)
            return Results.Unauthorized();
        registered = await database.RegisterInstallationAsync(installationId, machineName, body.MacAddress,
            SHA256.HashData(Encoding.UTF8.GetBytes(clientToken)), cancellationToken);
    }
    if (!registered)
        return Results.Unauthorized();

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

app.MapPost("/api/license-manager/v1/auth/login", (
    LicenseManagerLoginRequest? body,
    HttpContext context,
    LicenseManagerSessions sessions) =>
{
    var suppliedEmail = body?.Email?.Trim();
    var suppliedPassword = body?.Password ?? "";
    if (!string.Equals(suppliedEmail, licenseManagerEmail, StringComparison.OrdinalIgnoreCase) ||
        !HasMatchingSecret(suppliedPassword, licenseManagerPassword))
        return Results.Json(new { error = "Το email ή ο κωδικός δεν είναι σωστά." }, statusCode: StatusCodes.Status401Unauthorized);

    sessions.Create(context, licenseManagerEmail!);
    return Results.Ok(new { email = licenseManagerEmail, expiresInSeconds = LicenseManagerSessions.IdleTimeoutSeconds });
});

app.MapPost("/api/license-manager/v1/auth/logout", (HttpContext context, LicenseManagerSessions sessions) =>
{
    sessions.Revoke(context);
    return Results.Ok(new { status = "signed-out" });
});

var licenseManager = app.MapGroup("/api/license-manager/v1");
licenseManager.AddEndpointFilter(async (context, next) =>
{
    var sessions = context.HttpContext.RequestServices.GetRequiredService<LicenseManagerSessions>();
    if (!sessions.TryRefresh(context.HttpContext, out _))
        return Results.Unauthorized();
    return await next(context);
});

licenseManager.MapGet("/auth/session", (HttpContext context, LicenseManagerSessions sessions) =>
    Results.Ok(new { email = sessions.GetEmail(context), expiresInSeconds = LicenseManagerSessions.IdleTimeoutSeconds }));
licenseManager.MapPost("/auth/refresh", (HttpContext context, LicenseManagerSessions sessions) =>
    Results.Ok(new { email = sessions.GetEmail(context), expiresInSeconds = LicenseManagerSessions.IdleTimeoutSeconds }));

licenseManager.MapGet("/dashboard", async (CloudLicenseDatabase database, CancellationToken cancellationToken) =>
{
    var dashboard = await database.GetDashboardAsync(cancellationToken);
    return Results.Ok(new
    {
        customers = dashboard.Customers,
        licenses = dashboard.Licenses,
        installations = dashboard.Installations,
        editions = dashboard.Editions
    });
});

licenseManager.MapPost("/customers", async (
    CreateCloudCustomerRequest? body,
    CloudLicenseDatabase database,
    CancellationToken cancellationToken) =>
{
    var storeIdentifier = body?.StoreIdentifier?.Trim();
    var shopName = body?.ShopName?.Trim();
    var resellerName = body?.ResellerName?.Trim();
    var vatNumber = body?.VatNumber?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(storeIdentifier) || storeIdentifier.Length > 80 ||
        string.IsNullOrWhiteSpace(shopName) || shopName.Length > 160 ||
        string.IsNullOrWhiteSpace(resellerName) || resellerName.Length > 160 || vatNumber.Length > 32)
        return Results.BadRequest(new { error = "Συμπλήρωσε αναγνωριστικό καταστήματος, όνομα καταστήματος και μεταπωλητή. Το ΑΦΜ πρέπει να είναι έως 32 χαρακτήρες." });

    try
    {
        var customerId = await database.CreateCustomerAsync(storeIdentifier, shopName, resellerName, vatNumber, cancellationToken);
        return Results.Created($"/api/license-manager/v1/customers/{customerId}", new { customerId });
    }
    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        return Results.Conflict(new { error = "Το αναγνωριστικό καταστήματος χρησιμοποιείται ήδη." });
    }
});

licenseManager.MapPut("/customers/{customerId:guid}", async (
    Guid customerId,
    UpdateCloudCustomerRequest? body,
    CloudLicenseDatabase database,
    CancellationToken cancellationToken) =>
{
    var storeIdentifier = body?.StoreIdentifier?.Trim();
    var shopName = body?.ShopName?.Trim();
    var resellerName = body?.ResellerName?.Trim();
    var vatNumber = body?.VatNumber?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(storeIdentifier) || storeIdentifier.Length > 80 ||
        string.IsNullOrWhiteSpace(shopName) || shopName.Length > 160 ||
        string.IsNullOrWhiteSpace(resellerName) || resellerName.Length > 160 || vatNumber.Length > 32)
        return Results.BadRequest(new { error = "Συμπλήρωσε αναγνωριστικό καταστήματος, όνομα καταστήματος και μεταπωλητή. Το ΑΦΜ πρέπει να είναι έως 32 χαρακτήρες." });

    try
    {
        var updated = await database.UpdateCustomerAsync(customerId, storeIdentifier, shopName, resellerName, vatNumber, cancellationToken);
        return updated ? Results.Ok(new { status = "updated" }) : Results.NotFound(new { error = "Δεν βρέθηκε ενεργός πελάτης." });
    }
    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        return Results.Conflict(new { error = "Το αναγνωριστικό καταστήματος χρησιμοποιείται ήδη." });
    }
});

licenseManager.MapDelete("/customers/{customerId:guid}", async (
    Guid customerId,
    CloudLicenseDatabase database,
    CancellationToken cancellationToken) =>
{
    var deleted = await database.DeleteCustomerAsync(customerId, cancellationToken);
    return deleted
        ? Results.Ok(new { status = "deleted", message = "Ο πελάτης αρχειοθετήθηκε και οι άδειές του ανακλήθηκαν." })
        : Results.NotFound(new { error = "Δεν βρέθηκε ενεργός πελάτης." });
});

licenseManager.MapDelete("/customers/{customerId:guid}/permanent", async (
    Guid customerId,
    CloudLicenseDatabase database,
    CancellationToken cancellationToken) =>
{
    var deleted = await database.PermanentlyDeleteCustomerAsync(customerId, cancellationToken);
    return deleted
        ? Results.Ok(new { status = "permanently-deleted", message = "Το κατάστημα, οι άδειες, οι εγκαταστάσεις και το σχετικό ιστορικό διαγράφηκαν οριστικά." })
        : Results.NotFound(new { error = "Δεν βρέθηκε το κατάστημα." });
});

licenseManager.MapPost("/customers/{customerId:guid}/restore", async (
    Guid customerId,
    CloudLicenseDatabase database,
    CancellationToken cancellationToken) =>
{
    var restored = await database.RestoreCustomerAsync(customerId, cancellationToken);
    return restored
        ? Results.Ok(new { status = "restored", message = "Ο πελάτης επανενεργοποιήθηκε. Οι άδειες παραμένουν ανακλημένες μέχρι να ανανεωθούν." })
        : Results.NotFound(new { error = "Δεν βρέθηκε αρχειοθετημένος πελάτης." });
});

licenseManager.MapPost("/customers/{customerId:guid}/licenses", async (
    Guid customerId,
    CreateCloudLicenseForCustomerRequest? body,
    CloudLicenseDatabase database,
    CancellationToken cancellationToken) =>
{
    var expiresAtUtc = body?.ExpiresAtUtc?.UtcDateTime;
    if (body is null || body.EditionId == Guid.Empty || expiresAtUtc is null || expiresAtUtc <= DateTime.UtcNow)
        return Results.BadRequest(new { error = "Επίλεξε έκδοση άδειας και ημερομηνία λήξης στο μέλλον." });
    var (licenseId, error) = await database.CreateCustomerLicenseAsync(customerId, body.EditionId, expiresAtUtc.Value, cancellationToken);
    if (licenseId is null)
        return Results.Conflict(new { error = error == "customer-or-edition-not-found" ? "Ο πελάτης δεν είναι ενεργός ή η έκδοση άδειας δεν είναι διαθέσιμη." : "Δεν ήταν δυνατή η δημιουργία άδειας." });
    return Results.Created($"/api/license-manager/v1/licenses/{licenseId}", new { licenseId });
});

licenseManager.MapPost("/license-editions", async (
    UpsertCloudLicenseEditionRequest? body,
    CloudLicenseDatabase database,
    CancellationToken cancellationToken) =>
{
    var name = body?.Name?.Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 80 ||
        body!.MaxInstallations is < 1 or > 100 || body.DefaultDurationMonths is < 1 or > 120)
        return Results.BadRequest(new { error = "Συμπλήρωσε όνομα έκδοσης, 1–100 εγκαταστάσεις και διάρκεια 1–120 μηνών." });
    try
    {
        var editionId = await database.CreateLicenseEditionAsync(name, body.MaxInstallations, body.DefaultDurationMonths, cancellationToken);
        return Results.Created($"/api/license-manager/v1/license-editions/{editionId}", new { editionId });
    }
    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        return Results.Conflict(new { error = "Υπάρχει ήδη έκδοση με αυτό το όνομα." });
    }
});

licenseManager.MapPut("/license-editions/{editionId:guid}", async (
    Guid editionId,
    UpsertCloudLicenseEditionRequest? body,
    CloudLicenseDatabase database,
    CancellationToken cancellationToken) =>
{
    var name = body?.Name?.Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 80 ||
        body!.MaxInstallations is < 1 or > 100 || body.DefaultDurationMonths is < 1 or > 120)
        return Results.BadRequest(new { error = "Συμπλήρωσε όνομα έκδοσης, 1–100 εγκαταστάσεις και διάρκεια 1–120 μηνών." });
    try
    {
        var updated = await database.UpdateLicenseEditionAsync(editionId, name, body.MaxInstallations,
            body.DefaultDurationMonths, body.IsActive, cancellationToken);
        return updated ? Results.Ok(new { status = "updated" }) : Results.NotFound(new { error = "Δεν βρέθηκε η έκδοση άδειας." });
    }
    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        return Results.Conflict(new { error = "Υπάρχει ήδη έκδοση με αυτό το όνομα." });
    }
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

licenseManager.MapPut("/licenses/{licenseId:guid}", async (
    Guid licenseId,
    UpdateManagedLicenseRequest? body,
    CloudLicenseDatabase database,
    CancellationToken cancellationToken) =>
{
    var expiresAtUtc = body?.ExpiresAtUtc?.UtcDateTime;
    if (body is null || body.EditionId == Guid.Empty || expiresAtUtc is null || expiresAtUtc <= DateTime.UtcNow)
        return Results.BadRequest(new { error = "Επίλεξε έκδοση άδειας και ημερομηνία λήξης στο μέλλον." });
    var error = await database.UpdateLicenseTermsAsync(licenseId, body.EditionId, expiresAtUtc.Value, cancellationToken);
    return error switch
    {
        null => Results.Ok(new { status = "updated" }),
        "edition-not-found" => Results.Conflict(new { error = "Η έκδοση άδειας δεν είναι πλέον διαθέσιμη." }),
        "installation-limit" => Results.Conflict(new { error = "Η νέα έκδοση επιτρέπει λιγότερες εγκαταστάσεις από όσες είναι ήδη δεμένες." }),
        _ => Results.NotFound(new { error = "Δεν βρέθηκε ενεργή άδεια πελάτη." })
    };
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

licenseManager.MapPost("/installations/{installationId:guid}/rebind", async (
    Guid installationId,
    CloudLicenseDatabase database,
    CancellationToken cancellationToken) =>
{
    var requested = await database.RequestInstallationRebindAsync(installationId, cancellationToken);
    return requested
        ? Results.Ok(new { status = "rebind-pending" })
        : Results.NotFound(new { error = "Δεν βρέθηκε η εγκατάσταση." });
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

static bool IsValidEmailAddress(string? value) =>
    !string.IsNullOrWhiteSpace(value) && value.Length <= 254 &&
    Regex.IsMatch(value, @"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.CultureInvariant);

static bool TryVerifyLicenseCheckSignature(
    HttpRequest request,
    byte[] bodyBytes,
    Guid installationId,
    out byte[] publicKeyDer,
    out byte[] nonceHash)
{
    publicKeyDer = [];
    nonceHash = [];
    var publicKeyText = request.Headers["X-License-Public-Key"].ToString();
    var timestampText = request.Headers["X-License-Timestamp"].ToString();
    var nonceText = request.Headers["X-License-Nonce"].ToString();
    var signatureText = request.Headers["X-License-Signature"].ToString();
    if (publicKeyText.Length is < 1 or > 512 || timestampText.Length is < 1 or > 12 ||
        nonceText.Length is < 1 or > 128 || signatureText.Length is < 1 or > 256 ||
        !long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
        return false;

    DateTimeOffset signedAt;
    try
    {
        signedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }
    catch (ArgumentOutOfRangeException)
    {
        return false;
    }
    if ((DateTimeOffset.UtcNow - signedAt).Duration() > TimeSpan.FromMinutes(5))
        return false;

    byte[] nonceBytes;
    byte[] signature;
    try
    {
        publicKeyDer = Convert.FromBase64String(publicKeyText);
        signature = Convert.FromBase64String(signatureText);
        if (!TryDecodeBase64Url(nonceText, out nonceBytes))
            return false;
    }
    catch (FormatException)
    {
        return false;
    }

    if (publicKeyDer.Length is < 70 or > 200 || nonceBytes.Length is < 16 or > 64 || signature.Length != 64)
        return false;

    var bodyHash = Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();
    var canonical = $"POST\n/api/license/v1/check\n{installationId:D}\n{timestampText}\n{nonceText}\n{bodyHash}";
    try
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKeyDer, out var bytesRead);
        if (bytesRead != publicKeyDer.Length || key.KeySize != 256)
            return false;
        if (!key.VerifyData(Encoding.UTF8.GetBytes(canonical), signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            return false;
    }
    catch (CryptographicException)
    {
        return false;
    }

    nonceHash = SHA256.HashData(nonceBytes);
    return true;
}

static bool TryDecodeBase64Url(string value, out byte[] bytes)
{
    bytes = [];
    if (value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        return false;

    var base64 = value.Replace('-', '+').Replace('_', '/');
    if (base64.Length % 4 == 1)
        return false;
    base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
    try
    {
        bytes = Convert.FromBase64String(base64);
        return true;
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

static async Task<string> ReadLicenseSigningPrivateKeyAsync(IConfiguration configuration)
{
    var base64 = configuration["Cloud:LicenseSigningPrivateKeyBase64"];
    if (!string.IsNullOrWhiteSpace(base64))
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Cloud:LicenseSigningPrivateKeyBase64 must be Base64-encoded PEM, or configure Cloud:LicenseSigningPrivateKeyFile.",
                exception);
        }
    }

    var path = configuration["Cloud:LicenseSigningPrivateKeyFile"];
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        throw new InvalidOperationException(
            "Configure Cloud:LicenseSigningPrivateKeyBase64 or Cloud:LicenseSigningPrivateKeyFile for the RSA private key.");
    return await File.ReadAllTextAsync(path);
}

