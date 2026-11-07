using Acme.Application.Admin;
using Acme.Application.Audit;
using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Domain.Admin;
using Acme.Domain.Users;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Hierarchy;

/// <summary>One pharmacy a person may work as.</summary>
public sealed record WorkspaceDto(
    string PharmacyId,
    string PharmacyName,
    string Email,
    bool IsOwn,
    bool CanManage,
    bool IsCurrent);

/// <summary>The pharmacies available to a person, and which one they are in.</summary>
/// <param name="IsHeadOffice">
/// Whether the signed-in account runs branches. Read from the account itself,
/// not from the pharmacy currently being worked as — somebody acting as one of
/// their branches is still a head office, and the badge should not blink off
/// when they switch.
/// </param>
public sealed record WorkspacesDto(
    string CurrentPharmacyId,
    string CurrentPharmacyName,
    bool IsActingAsAnother,
    IReadOnlyList<WorkspaceDto> Workspaces,
    bool IsHeadOffice = false);

/// <summary>A token scoped to a chosen pharmacy.</summary>
public sealed record SwitchResultDto(
    string Token,
    DateTime ExpiresAtUtc,
    string PharmacyId,
    string PharmacyName);

/// <summary>
/// Choosing which pharmacy you are working as.
/// </summary>
/// <remarks>
/// <para>
/// A generalisation of manage-as rather than a second mechanism. Switching
/// issues the same delegated token — subject is the effective pharmacy, actor
/// is you — so every endpoint that already reads <c>sub</c> to decide whose
/// data it is looking at keeps working with no change. What differs is the
/// gate: manage-as asks "is this pharmacy your child", switching asks "do you
/// have a membership".
/// </para>
/// <para>
/// <b>The pharmacy id is never trusted from the caller.</b> It arrives as a
/// request, is resolved against a membership row, and only then becomes a
/// token. That check runs again on every refresh, so revoking a membership
/// ends the access within one token lifetime rather than whenever the browser
/// next chooses to ask.
/// </para>
/// </remarks>
public sealed class PharmacyContextService(
    IUserRepository users,
    IPharmacyMembershipRepository memberships,
    IJwtTokenService tokens,
    IAdminAuditRepository audit,
    IAuditScope auditScope,
    ILogger<PharmacyContextService> log)
{
    /// <summary>
    /// How long a switched session lasts before the browser must ask again.
    /// </summary>
    /// <remarks>
    /// Matches the ordinary access-token lifetime rather than the shorter
    /// manage-as window. Working as a pharmacy you belong to is a normal mode
    /// of use, not a support intervention — but the token still cannot be
    /// refreshed on its own, so membership is rechecked when the session is.
    /// </remarks>
    private static readonly TimeSpan SwitchWindow = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Every pharmacy this person may work as.
    /// </summary>
    /// <param name="userId">The real person, from the actor claim when delegated.</param>
    /// <param name="currentPharmacyId">Whichever pharmacy they are in right now.</param>
    public async Task<Result<WorkspacesDto>> ListWorkspacesAsync(
        string userId, string currentPharmacyId, CancellationToken ct)
    {
        var rows = await EnsureSelfMembershipAsync(userId, ct);

        if (rows.Count == 0)
            return Result<WorkspacesDto>.NotFound("No pharmacies are available for this account.");

        // One lookup per pharmacy, but the list is a handful of rows — a person
        // works across a few pharmacies, not thousands.
        var items = new List<WorkspaceDto>();
        foreach (var row in rows)
        {
            var pharmacy = await users.FindByIdAsync(row.PharmacyId, ct);
            if (pharmacy is null)
            {
                // A membership pointing at a deleted account. Skipped rather
                // than failing the whole list, which would lock somebody out of
                // their own pharmacy because of an unrelated stale row.
                log.LogWarning(
                    "Membership {Id} points at missing pharmacy {Pharmacy}", row.Id, row.PharmacyId);
                continue;
            }

            items.Add(new WorkspaceDto(
                PharmacyId: pharmacy.Id,
                PharmacyName: pharmacy.PharmacyDisplayName,
                Email: pharmacy.Email.Value,
                IsOwn: row.IsSelf,
                CanManage: row.CanManage,
                IsCurrent: string.Equals(pharmacy.Id, currentPharmacyId, StringComparison.Ordinal)));
        }

        if (items.Count == 0)
            return Result<WorkspacesDto>.NotFound("No pharmacies are available for this account.");

        var current = items.FirstOrDefault(w => w.IsCurrent)
                      ?? items.FirstOrDefault(w => w.IsOwn)
                      ?? items[0];

        // The person, not the pharmacy they happen to be inside. A head office
        // acting as one of its branches is still a head office; reading this off
        // the current workspace would make the badge blink off on every switch.
        var self = await users.FindByIdAsync(userId, ct);

        return Result<WorkspacesDto>.Success(new WorkspacesDto(
            CurrentPharmacyId: current.PharmacyId,
            CurrentPharmacyName: current.PharmacyName,
            IsActingAsAnother: !current.IsOwn,
            Workspaces: items.OrderByDescending(w => w.IsOwn).ThenBy(w => w.PharmacyName).ToList(),
            IsHeadOffice: self?.IsHeadOffice ?? false));
    }

    /// <summary>
    /// Issues a token scoped to a pharmacy the caller belongs to.
    /// </summary>
    /// <remarks>
    /// Returns the same shape whether the target is your own pharmacy or
    /// another you are a member of. Switching back to your own is a switch like
    /// any other, so the caller has one path rather than a special case that
    /// could be forgotten.
    /// </remarks>
    public async Task<Result<SwitchResultDto>> SwitchAsync(
        string userId, string pharmacyId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pharmacyId))
            return Result<SwitchResultDto>.Invalid("Choose a pharmacy to work as.");

        var actor = await users.FindByIdAsync(userId, ct);
        if (actor is null) return Result<SwitchResultDto>.NotFound("Account not found.");

        if (!actor.CanSignIn)
            return Result<SwitchResultDto>.Forbidden("This account cannot sign in.");

        // Self-heal before the gate, so switching back to your own pharmacy
        // works even on an account created by a path that predates memberships.
        var isSelf = string.Equals(userId, pharmacyId, StringComparison.Ordinal);
        if (isSelf) await EnsureSelfMembershipAsync(userId, ct);

        // The audit row names the shop being entered, whatever the outcome —
        // a refused attempt on somebody else's pharmacy is the row that matters.
        auditScope.SetPharmacy(pharmacyId);
        auditScope.SetEntity("pharmacy", pharmacyId);

        var pharmacy = await users.FindByIdAsync(pharmacyId, ct);

        // The gate. Nothing above this line consulted the caller about what
        // they are allowed to reach.
        //
        // Two ways through it, and they are not the same thing. A membership is
        // a pharmacy-side grant; platform staff need no grant because support
        // work reaches pharmacies that have never heard of them. Both are
        // audited; only the first is something a customer can hand out.
        var membership = await memberships.FindAsync(userId, pharmacyId, ct);
        var asStaff = membership is null && actor.Role.IsStaff();

        // Staff entering a shop without a grant is impersonation, reviewed as
        // admin work; a member (a head office, a colleague) switching is not.
        if (actor.Role.IsStaff() && membership is null) auditScope.SetAction("admin.impersonate.start");

        if (membership is null && !asStaff)
        {
            // Deliberately the same message as a missing pharmacy. Telling
            // somebody "that pharmacy exists but is not yours" confirms the id
            // is real, which is the one thing probing would be looking for.
            log.LogWarning(
                "{User} tried to work as {Pharmacy} without a membership", userId, pharmacyId);
            return Result<SwitchResultDto>.Forbidden("You do not have access to that pharmacy.");
        }

        if (pharmacy is null)
            return Result<SwitchResultDto>.NotFound("That pharmacy no longer exists.");

        // A pharmacy-side privilege must never reach a staff account.
        //
        // Nothing is supposed to grant a membership over an administrator, but
        // "nothing is supposed to" is not a control. The check lives here
        // because this is the single place a token for another account is
        // minted — so however a stray membership came to exist, it cannot be
        // spent. Staff reaching staff is a separate question, and still refused:
        // acting as another administrator would launder one person's actions
        // into another's name.
        if (pharmacy.Role.IsStaff() && !isSelf)
        {
            log.LogWarning(
                "{User} tried to work as staff account {Pharmacy}", userId, pharmacyId);
            return Result<SwitchResultDto>.Forbidden("You do not have access to that pharmacy.");
        }

        if (!pharmacy.IsActive)
            return Result<SwitchResultDto>.Forbidden("That pharmacy is deactivated.");

        var (token, expiresAt) = tokens.IssueDelegated(pharmacy, actor, SwitchWindow);

        log.LogInformation(
            "{Actor} is now working as {Pharmacy} ({How})",
            PersonalData.Mask(actor.Email.Value), PersonalData.Mask(pharmacy.Email.Value),
            asStaff ? "platform staff" : membership!.Role.ToString());

        // Audited only when it is somebody else's pharmacy. Recording every
        // return to your own account would bury the entries that matter.
        if (isSelf) auditScope.Suppress();
        else
            auditScope.AddMeta("via", asStaff ? "staff" : $"membership:{membership!.Role}");

        if (!isSelf)
            await RecordAsync(actor, pharmacy, AdminAction.StartActingAs,
                asStaff ? "Platform staff support" : $"Membership: {membership!.Role}", ct);

        return Result<SwitchResultDto>.Success(new SwitchResultDto(
            token, expiresAt, pharmacy.Id, pharmacy.PharmacyDisplayName));
    }

    /// <summary>
    /// Records the end of a period spent working as another pharmacy.
    /// </summary>
    /// <remarks>
    /// Paired with the start entry so the log shows a window rather than a
    /// moment. Best-effort: leaving is a cookie delete in the browser, and a
    /// failure to write the entry must not trap somebody inside another
    /// pharmacy.
    /// </remarks>
    public async Task StopActingAsAsync(string userId, string pharmacyId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pharmacyId)
            || string.Equals(userId, pharmacyId, StringComparison.Ordinal))
        {
            // Leaving your own pharmacy is not leaving anything.
            auditScope.Suppress();
            return;
        }

        auditScope.SetPharmacy(pharmacyId);
        auditScope.SetEntity("pharmacy", pharmacyId);

        var actor = await users.FindByIdAsync(userId, ct);
        var pharmacy = await users.FindByIdAsync(pharmacyId, ct);
        if (actor is null || pharmacy is null) return;

        if (actor.Role.IsStaff()
            && await memberships.FindAsync(userId, pharmacyId, ct) is null)
            auditScope.SetAction("admin.impersonate.stop");

        await RecordAsync(actor, pharmacy, AdminAction.StopActingAs, null, ct);
    }

    /// <summary>Writes one audit entry. Never throws.</summary>
    private async Task RecordAsync(
        User actor, User pharmacy, AdminAction action, string? detail, CancellationToken ct)
    {
        try
        {
            await audit.AddAsync(
                AdminAuditEntry.Record(
                    actorId: actor.Id,
                    actorEmail: actor.Email.Value,
                    action: action,
                    targetUserId: pharmacy.Id,
                    targetEmail: pharmacy.Email.Value,
                    detail: detail),
                ct);
        }
        catch (Exception ex)
        {
            // The switch itself has already been decided. Failing it now
            // because the log is unavailable would deny legitimate access for
            // a reason the person cannot act on.
            log.LogError(ex,
                "Could not record {Action} by {Actor} on {Pharmacy}",
                action, actor.Id, pharmacy.Id);
        }
    }

    /// <summary>
    /// Returns this person's memberships, creating the self-membership if absent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There are six places in this codebase that create a <c>User</c> —
    /// registration, two seeders, the admin console, invitations, and
    /// sub-pharmacy creation. Granting the self-membership at each of them is
    /// six chances to forget, and the symptom of forgetting is an account that
    /// cannot open its own pharmacy: total, and only visible to the person
    /// affected.
    /// </para>
    /// <para>
    /// So it is granted where it is needed instead. Idempotent, guarded by the
    /// unique index on (userId, pharmacyId), and a write on a read path only
    /// ever once per account.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<PharmacyMembership>> EnsureSelfMembershipAsync(
        string userId, CancellationToken ct)
    {
        var rows = await memberships.ListForUserAsync(userId, ct);
        if (rows.Any(r => r.IsSelf)) return rows;

        try
        {
            await memberships.AddAsync(PharmacyMembership.Self(userId), ct);
            log.LogInformation("Granted the missing self-membership for {User}", userId);
        }
        catch (Exception ex)
        {
            // A duplicate here means another request won the race, which is the
            // outcome wanted. Re-reading below picks up whichever row landed.
            log.LogDebug(ex, "Self-membership for {User} already existed", userId);
        }

        return await memberships.ListForUserAsync(userId, ct);
    }

    /// <summary>
    /// Confirms a person may still work as a pharmacy.
    /// </summary>
    /// <remarks>
    /// Called when a session is refreshed. Without it a switch would outlive
    /// the membership that justified it: the browser holds a pharmacy id, and
    /// re-issuing a token from that alone would mean a revoked membership kept
    /// working until the person happened to switch again.
    /// </remarks>
    public async Task<bool> StillPermittedAsync(
        string userId, string pharmacyId, CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(pharmacyId)
        && await memberships.FindAsync(userId, pharmacyId, ct) is not null;
}
