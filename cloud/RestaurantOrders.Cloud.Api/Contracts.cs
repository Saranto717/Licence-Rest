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
public sealed record CreateCloudLicenseRequest(
    string CustomerName, string? VatNumber, string? ActivationCode, int DurationMonths,
    int MaxInstallations = 1, string? Comments = null);
public sealed record UpdateCloudLicenseRequest(
    string CustomerName, string? VatNumber, DateTimeOffset? ExpiresAtUtc,
    int MaxInstallations, string? Comments);
public sealed record RenewCloudLicenseRequest(int DurationMonths);
public sealed record AssignCloudLicenseRequest(Guid LicenseId);
public sealed record ManagedLicenseView(Guid Id, string CustomerName, string VatNumber, string Status,
    DateTime? ExpiresAtUtc, int MaxInstallations, int ActiveInstallations, DateTime CreatedAtUtc, string Comments);
public sealed record ManagedInstallationView(Guid InstallationId, string MachineName, DateTime FirstSeenAtUtc,
    DateTime LastSeenAtUtc, Guid? LicenseId, string? CustomerName, string? LicenseStatus, bool IsAssigned);
public sealed record CreateLicenseResult(Guid? LicenseId, bool ActivationCodeAlreadyAssigned);
