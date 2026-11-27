using System.Text.Json;

namespace RestaurantOrders.Cloud.Api;

public sealed record CreateStoreRequest(string? Name);

public sealed record SyncBatchRequest(IReadOnlyList<SyncEventRequest?>? Events);

public sealed record SyncEventRequest(
    Guid EventId,
    string? EntityType,
    string? EntityId,
    string? EventType,
    int SchemaVersion,
    DateTimeOffset OccurredAtUtc,
    JsonElement Payload);

public sealed record SyncBatchResponse(Guid StoreId, int Accepted, int Duplicates);

public sealed record RemoteLicenseCheckRequest(string InstallationId, string MachineName, string MacAddress);
public sealed record LicenseManagerLoginRequest(string? Email, string? Password);
public sealed class RemoteLicenseCheckResponse
{
    public string Status { get; set; } = "pending";
    public SignedLicenseContract? License { get; set; }
    public string? Message { get; set; }
}
public sealed class SignedLicenseContract
{
    public string Payload { get; set; } = "";
    public string Signature { get; set; } = "";
}
public sealed class CloudLicensePayload
{
    public int SchemaVersion { get; set; } = 2;
    public string LicenseId { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string VatNumber { get; set; } = "";
    public string InstallationId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public DateTime IssuedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public string RemoteStatus { get; set; } = "active";
    public DateTime? LeaseExpiresAtUtc { get; set; }
}
public sealed record CreateCloudLicenseRequest(string CustomerName, string? VatNumber, DateTimeOffset? ExpiresAtUtc, int MaxInstallations = 1);
public sealed record RenewCloudLicenseRequest(DateTimeOffset? ExpiresAtUtc);
public sealed record AssignCloudLicenseRequest(Guid LicenseId);
public sealed record CreateCloudCustomerRequest(string? StoreIdentifier, string? ShopName, string? ResellerName, string? VatNumber, Guid? CategoryId = null);
public sealed record UpdateCloudCustomerRequest(string? StoreIdentifier, string? ShopName, string? ResellerName, string? VatNumber, Guid? CategoryId = null);
public sealed record CreateLicenseManagerCategoryRequest(string? Name);
public sealed record UpdateLicenseManagerCategoryRequest(string? Name);
public sealed record CreateCloudLicenseForCustomerRequest(Guid EditionId, DateTimeOffset? ExpiresAtUtc);
public sealed record UpsertCloudLicenseEditionRequest(string? Name, int MaxInstallations, int DefaultDurationMonths, bool IsActive = true);
public sealed record UpdateManagedLicenseRequest(Guid EditionId, DateTimeOffset? ExpiresAtUtc);
public sealed record ManagedCustomerView(Guid Id, string StoreIdentifier, string ShopName, string ResellerName,
    string VatNumber, bool IsActive, int ActiveLicenseCount, int ActiveInstallationCount, DateTime CreatedAtUtc,
    Guid? CategoryId = null, string? CategoryName = null);
public sealed record ManagedLicenseManagerCategoryView(Guid Id, string Name, int CustomerCount, DateTime CreatedAtUtc);
public sealed record ManagedCustomerAuditView(long Id, Guid CustomerId, string Action, DateTime CreatedAtUtc,
    Guid? LicenseId, Guid? InstallationId);
public sealed record ManagedLicenseEditionView(Guid Id, string Name, int MaxInstallations,
    int DefaultDurationMonths, bool IsActive, int LicenseCount);
public sealed record ManagedLicenseView(Guid Id, Guid CustomerId, string StoreIdentifier, string CustomerName,
    string ResellerName, string VatNumber, Guid? EditionId, string? EditionName, string Status,
    DateTime? ExpiresAtUtc, int MaxInstallations, int ActiveInstallations, DateTime CreatedAtUtc);
public sealed record ManagedInstallationView(Guid InstallationId, string MachineName, DateTime FirstSeenAtUtc,
    DateTime LastSeenAtUtc, Guid? LicenseId, string? CustomerName, string? StoreIdentifier,
    string? LicenseStatus, bool IsAssigned, bool RebindPending);
public sealed record LicenseManagerDashboard(IReadOnlyList<ManagedCustomerView> Customers,
    IReadOnlyList<ManagedLicenseView> Licenses, IReadOnlyList<ManagedInstallationView> Installations,
    IReadOnlyList<ManagedLicenseEditionView> Editions,
    IReadOnlyList<ManagedLicenseManagerCategoryView> Categories,
    IReadOnlyList<ManagedCustomerAuditView> Audit);
