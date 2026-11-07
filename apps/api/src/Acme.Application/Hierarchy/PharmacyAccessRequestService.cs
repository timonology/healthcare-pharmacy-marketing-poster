using Acme.Application.Admin;
using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Domain.Admin;
using Acme.Domain.Common;
using Acme.Domain.Users;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Hierarchy;

/// <summary>One request, as the requester sees it.</summary>
public sealed record AccessRequestDto(
    string Id,
    string PharmacyId,
    string PharmacyEmail,
    string PharmacyName,
    string Status,
    string Reason,
    string? ReviewNote,
    DateTime RequestedAtUtc,
    DateTime? ReviewedAtUtc);

/// <summary>One request, as an administrator sees it.</summary>
public sealed record AccessRequestReviewDto(
    string Id,
    string UserId,
    string UserEmail,
    string PharmacyId,
    string PharmacyEmail,
    string PharmacyName,
    string Status,
    string Reason,
    string? ReviewNote,
    string? ReviewedByEmail,
    DateTime RequestedAtUtc,
    DateTime? ReviewedAtUtc);

/// <summary>
/// Asking for access to a pharmacy, and the decision that grants it.
/// </summary>
/// <remarks>
/// <para>
/// The one route by which somebody comes to read a pharmacy's patient list
/// without a head-office relationship. Marketing25 staff decide rather than the
/// pharmacy, because a locum covering a shift may have no existing relationship
/// with anybody in the system — but that means the decision is a platform
/// action, and it is audited like one.
/// </para>
/// <para>
/// <b>The pharmacy is named by email, and the answer never says whether it
/// exists.</b> Accepting an account id would mean the requester already had one,
/// which they will not; accepting an email and then reporting "no such
/// pharmacy" would turn this into a way to test which addresses are registered.
/// So it behaves like the password-reset endpoint: same response either way,
/// and only the existence of a row differs.
/// </para>
/// </remarks>
public sealed class PharmacyAccessRequestService(
    IUserRepository users,
    IPharmacyMembershipRepository memberships,
    IPharmacyAccessRequestRepository requests,
    IAdminAuditRepository audit,
    ILogger<PharmacyAccessRequestService> log)
{
    /// <summary>
    /// Raises a request. Reports the same thing whatever the address.
    /// </summary>
    /// <remarks>
    /// Returns no indication of what happened, deliberately — there is nothing
    /// here for a caller to surface by accident.
    /// </remarks>
    public async Task RequestAsync(
        string userId, string pharmacyEmail, string reason, CancellationToken ct)
    {
        var requester = await users.FindByIdAsync(userId, ct);
        if (requester is null) return;

        Email parsed;
        try { parsed = Email.Create(pharmacyEmail); }
        catch (DomainException)
        {
            log.LogInformation("Access requested for an unparseable address");
            return;
        }

        var pharmacy = await users.FindByEmailAsync(parsed, ct);

        if (pharmacy is null)
        {
            log.LogInformation(
                "{User} requested access to {Email}, which has no account", userId, PersonalData.Mask(parsed.Value));
            return;
        }

        // A pharmacy-side privilege must never reach a staff account — the same
        // rule the switch gate enforces, applied where the grant is asked for
        // rather than only where it is spent.
        if (pharmacy.Role.IsStaff())
        {
            log.LogWarning(
                "{User} requested access to staff account {Email}", userId, PersonalData.Mask(parsed.Value));
            return;
        }

        if (await memberships.FindAsync(userId, pharmacy.Id, ct) is not null)
        {
            log.LogInformation("{User} already belongs to {Pharmacy}", userId, pharmacy.Id);
            return;
        }

        // One open question at a time. Without this, clicking twice fills the
        // review queue with duplicates of the same request.
        if (await requests.FindPendingAsync(userId, pharmacy.Id, ct) is not null)
        {
            log.LogInformation("{User} already has a pending request for {Pharmacy}", userId, pharmacy.Id);
            return;
        }

        PharmacyAccessRequest request;
        try
        {
            request = PharmacyAccessRequest.Raise(
                userId, requester.Email.Value, pharmacy.Id, pharmacy.Email.Value, reason);
        }
        catch (DomainException ex)
        {
            // A bad reason is the requester's own input and safe to reflect —
            // it says nothing about whether the pharmacy exists. But this method
            // returns nothing, so it is logged and the caller validates length
            // before calling. See the controller.
            log.LogInformation("Access request refused: {Reason}", ex.Message);
            return;
        }

        await requests.AddAsync(request, ct);

        await RecordAsync(
            requester, pharmacy, AdminAction.RequestPharmacyAccess, request.Reason, ct);

        log.LogInformation(
            "{User} requested access to {Pharmacy}", PersonalData.Mask(requester.Email.Value), PersonalData.Mask(pharmacy.Email.Value));
    }

    /// <summary>What this person has asked for, and how it went.</summary>
    public async Task<Result<IReadOnlyList<AccessRequestDto>>> ListMineAsync(
        string userId, CancellationToken ct)
    {
        var rows = await requests.ListForUserAsync(userId, ct);
        var items = new List<AccessRequestDto>();

        foreach (var r in rows)
        {
            var pharmacy = await users.FindByIdAsync(r.PharmacyId, ct);
            items.Add(new AccessRequestDto(
                r.Id, r.PharmacyId, r.PharmacyEmail,
                pharmacy?.PharmacyDisplayName ?? r.PharmacyEmail,
                r.Status.ToString(), r.Reason, r.ReviewNote, r.CreatedAtUtc, r.ReviewedAtUtc));
        }

        return Result<IReadOnlyList<AccessRequestDto>>.Success(items);
    }

    /// <summary>The review queue.</summary>
    public async Task<Result<IReadOnlyList<AccessRequestReviewDto>>> ListForReviewAsync(
        bool pendingOnly, int take, CancellationToken ct)
    {
        var rows = await requests.ListForReviewAsync(pendingOnly, take, ct);
        var items = new List<AccessRequestReviewDto>();

        foreach (var r in rows)
        {
            var pharmacy = await users.FindByIdAsync(r.PharmacyId, ct);
            items.Add(new AccessRequestReviewDto(
                r.Id, r.UserId, r.UserEmail, r.PharmacyId, r.PharmacyEmail,
                pharmacy?.PharmacyDisplayName ?? r.PharmacyEmail,
                r.Status.ToString(), r.Reason, r.ReviewNote, r.ReviewedByEmail,
                r.CreatedAtUtc, r.ReviewedAtUtc));
        }

        return Result<IReadOnlyList<AccessRequestReviewDto>>.Success(items);
    }

    /// <summary>
    /// Approves or rejects a request. Approval is what creates the membership.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The membership is granted as <see cref="PharmacyMembershipRole.Member"/>,
    /// never HeadOfficeAdmin. Someone who had to ask for access should not
    /// arrive able to hand it out to others; raising them further is a separate,
    /// deliberate act.
    /// </para>
    /// <para>
    /// The membership is written before the request is marked approved. If the
    /// order were reversed and the grant failed, the requester would be told
    /// yes and have nothing — the worst of the two failure modes, because it
    /// looks resolved to everyone.
    /// </para>
    /// </remarks>
    public async Task<Result<bool>> ReviewAsync(
        string reviewerId, string requestId, bool approve, string? note, CancellationToken ct)
    {
        var reviewer = await users.FindByIdAsync(reviewerId, ct);
        if (reviewer is null) return Result<bool>.NotFound("Account not found.");

        if (!reviewer.Role.IsStaff())
            return Result<bool>.Forbidden("Only Marketing25 staff can review access requests.");

        var request = await requests.FindByIdAsync(requestId, ct);
        if (request is null) return Result<bool>.NotFound("That request no longer exists.");

        if (!request.IsPending)
            return Result<bool>.Invalid("That request has already been decided.");

        var pharmacy = await users.FindByIdAsync(request.PharmacyId, ct);
        if (pharmacy is null)
            return Result<bool>.Invalid("That pharmacy no longer exists.");

        if (approve)
        {
            // Re-checked at decision time rather than trusted from when the
            // request was raised. A pharmacy that has since become a staff
            // account, or a requester who has since been granted access another
            // way, both change the answer.
            if (pharmacy.Role.IsStaff())
                return Result<bool>.Forbidden("Access cannot be granted to a staff account.");

            if (await memberships.FindAsync(request.UserId, request.PharmacyId, ct) is null)
            {
                await memberships.AddAsync(
                    PharmacyMembership.Grant(
                        userId: request.UserId,
                        pharmacyId: request.PharmacyId,
                        role: PharmacyMembershipRole.Member,
                        grantedBy: reviewerId),
                    ct);
            }
        }

        try
        {
            request.Decide(
                approve ? AccessRequestStatus.Approved : AccessRequestStatus.Rejected,
                reviewerId, reviewer.Email.Value, note);
        }
        catch (DomainException ex)
        {
            return Result<bool>.Invalid(ex.Message);
        }

        await requests.UpdateAsync(request, ct);

        var requester = await users.FindByIdAsync(request.UserId, ct);
        if (requester is not null)
        {
            await RecordAsync(
                reviewer, requester, AdminAction.ReviewPharmacyAccess,
                $"{(approve ? "Approved" : "Rejected")} access to {pharmacy.Email.Value}"
                + (string.IsNullOrWhiteSpace(note) ? "" : $" — {note.Trim()}"),
                ct);
        }

        log.LogInformation(
            "{Reviewer} {Outcome} {User}'s request for {Pharmacy}",
            PersonalData.Mask(reviewer.Email.Value), approve ? "approved" : "rejected",
            PersonalData.Mask(request.UserEmail), PersonalData.Mask(pharmacy.Email.Value));

        return Result<bool>.Success(true);
    }

    /// <summary>Writes one audit entry. Never throws.</summary>
    private async Task RecordAsync(
        User actor, User target, AdminAction action, string? detail, CancellationToken ct)
    {
        try
        {
            await audit.AddAsync(
                AdminAuditEntry.Record(
                    actorId: actor.Id,
                    actorEmail: actor.Email.Value,
                    action: action,
                    targetUserId: target.Id,
                    targetEmail: target.Email.Value,
                    detail: detail),
                ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "Could not record {Action} by {Actor} on {Target}", action, actor.Id, target.Id);
        }
    }
}
