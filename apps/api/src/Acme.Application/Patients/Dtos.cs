namespace Acme.Application.Patients;

public sealed record PatientDto(
    string Id,
    string OwnerId,
    string FullName,
    string? Email,
    string? Phone,
    string Notes,
    IReadOnlyList<string> GroupIds,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    // Security review R2-06: when consent to marketing email was recorded, and
    // whether this address has unsubscribed from this pharmacy.
    DateTime? MarketingConsentAtUtc = null,
    string? MarketingConsentSource = null,
    bool Unsubscribed = false);

public sealed record PatientSummaryDto(
    string Id,
    string FullName,
    string? Email,
    string? Phone,
    IReadOnlyList<string> GroupIds,
    DateTime UpdatedAtUtc);

public sealed record UpsertPatientRequest(
    string FullName,
    string? Email,
    string? Phone,
    string? Notes,
    IReadOnlyList<string>? GroupIds,
    // Null leaves consent as it is; true records it (first date kept); false clears it.
    bool? MarketingConsent = null);

public sealed record BulkImportRequest(IReadOnlyList<UpsertPatientRequest> Patients);

public sealed record BulkImportResult(int Imported, int Skipped, IReadOnlyList<string> Errors);

public sealed record AddRemoveGroupRequest(string GroupId);

public sealed record BulkAddToGroupRequest(IReadOnlyList<string> PatientIds, string GroupId);

public sealed record BulkDeleteRequest(IReadOnlyList<string> Ids);

/// <param name="OwnerId">Empty for a global group — it belongs to the platform.</param>
/// <param name="PatientCount">
/// The requesting pharmacy's members only. For a global group this is their own
/// count, never the platform-wide total.
/// </param>
/// <param name="CanEdit">
/// False for a global group. The pharmacy can still add and remove their own
/// patients; only the name and description are off limits.
/// </param>
public sealed record PatientGroupDto(
    string Id,
    string OwnerId,
    string Name,
    string Description,
    int PatientCount,
    bool IsGlobal,
    bool CanEdit,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record UpsertPatientGroupRequest(string Name, string? Description);
