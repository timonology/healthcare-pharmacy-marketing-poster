using Acme.Domain.Users;

namespace Acme.Application.Invitations;

public interface IPharmacyInvitationRepository
{
    Task AddAsync(PharmacyInvitation invitation, CancellationToken ct);
    Task UpdateAsync(PharmacyInvitation invitation, CancellationToken ct);

    /// <summary>Looks up by token hash. The raw token is never stored, so this is the only way in.</summary>
    Task<PharmacyInvitation?> FindByTokenHashAsync(string tokenHash, CancellationToken ct);

    /// <summary>Every invitation for an account, newest first.</summary>
    Task<IReadOnlyList<PharmacyInvitation>> ListForUserAsync(string userId, CancellationToken ct);

    /// <summary>The outstanding invitations for a set of accounts, for the admin list.</summary>
    /// <remarks>
    /// Batched rather than one query per row: the invited-pharmacists table
    /// shows a status per account, and a per-row lookup would make it N+1 over
    /// a list that is expected to be long after a bulk import.
    /// </remarks>
    Task<IReadOnlyDictionary<string, PharmacyInvitation>> LatestForUsersAsync(
        IReadOnlyList<string> userIds, CancellationToken ct);
}

/* ------------------------------------------------------------------ *
 * DTOs
 * ------------------------------------------------------------------ */

/// <summary>
/// What a SuperAdmin supplies for one pharmacist.
/// </summary>
/// <param name="FCode">
/// ODS / F-code. The same field the pharmacy profile already calls
/// <c>SonarFCode</c> — reused rather than duplicated, so a pharmacist invited
/// here and one who self-registered are the same shape of record.
/// </param>
public sealed record InvitePharmacistRequest(
    string FullName,
    string Email,
    string? FCode = null,
    string? Address = null,
    string? PostCode = null,
    string? Phone = null,
    string? PharmacyName = null);

public sealed record BulkInviteRequest(IReadOnlyList<InvitePharmacistRequest> Pharmacists);

/// <summary>Parsed from an uploaded CSV before anything is created.</summary>
public sealed record CsvPreviewDto(
    IReadOnlyList<InvitePharmacistRequest> Valid,
    IReadOnlyList<CsvRowError> Errors,
    int TotalRows);

/// <param name="Line">1-based line number in the uploaded file, header included.</param>
public sealed record CsvRowError(int Line, string Reason, string? Email);

/// <summary>The outcome of one invitation within a batch.</summary>
/// <remarks>
/// Per-row rather than a single pass/fail. A 200-row import where three
/// addresses already exist should create 197 accounts and name the three, not
/// abort — re-running a whole import to fix three typos is how duplicates get
/// created.
/// </remarks>
public sealed record InviteResultDto(
    string Email,
    bool Success,
    string? UserId,
    string? Error,
    bool EmailSent);

public sealed record BulkInviteResultDto(
    int Invited,
    int Failed,
    int EmailsSent,
    IReadOnlyList<InviteResultDto> Results);

public enum InvitationStatus
{
    /// <summary>Sent, still valid, not yet used.</summary>
    Invited = 0,

    /// <summary>The pharmacist set a password and signed in.</summary>
    Activated = 1,

    /// <summary>The link ran out before it was used. Resend to issue a new one.</summary>
    Expired = 2,
}

public sealed record InvitedPharmacistDto(
    string UserId,
    string Email,
    string DisplayName,
    string PharmacyName,
    string? FCode,
    string PostCode,
    InvitationStatus Status,
    DateTime? InvitedAtUtc,
    DateTime? ExpiresAtUtc,
    DateTime? ActivatedAtUtc,
    string? InvitedByEmail,
    int TimesSent);

public sealed record InvitedListDto(
    IReadOnlyList<InvitedPharmacistDto> Items,
    long Total,
    int Skip,
    int Take);

/// <summary>What the activation page needs before the password is set.</summary>
/// <remarks>
/// Deliberately thin. This endpoint is reachable by anyone holding the token,
/// so it returns only enough to confirm they are activating the right account —
/// no phone number, no address, nothing that would turn a leaked link into a
/// data disclosure.
/// </remarks>
public sealed record InvitationPreviewDto(
    string Email,
    string DisplayName,
    string PharmacyName,
    DateTime ExpiresAtUtc);

public sealed record AcceptInvitationRequest(string Token, string Password);
