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
    DateTime UpdatedAtUtc);

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
    IReadOnlyList<string>? GroupIds);

public sealed record BulkImportRequest(IReadOnlyList<UpsertPatientRequest> Patients);

public sealed record BulkImportResult(int Imported, int Skipped, IReadOnlyList<string> Errors);

public sealed record AddRemoveGroupRequest(string GroupId);

public sealed record BulkAddToGroupRequest(IReadOnlyList<string> PatientIds, string GroupId);

public sealed record BulkDeleteRequest(IReadOnlyList<string> Ids);

public sealed record PatientGroupDto(
    string Id,
    string OwnerId,
    string Name,
    string Description,
    int PatientCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record UpsertPatientGroupRequest(string Name, string? Description);
