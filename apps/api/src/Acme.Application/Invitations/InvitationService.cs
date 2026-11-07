using Acme.Application.Admin;
using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Application.Messaging;
using Acme.Domain.Admin;
using Acme.Domain.Common;
using Acme.Domain.Users;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Invitations;

/// <summary>
/// Inviting pharmacists, and letting them claim their account.
/// </summary>
/// <remarks>
/// <para>
/// Two audiences with opposite trust levels. Everything that creates or resends
/// is SuperAdmin-only and checked here as well as by the controller policy.
/// <see cref="PreviewAsync"/> and <see cref="AcceptAsync"/> are anonymous by
/// necessity — the pharmacist has no account yet — so the token is the only
/// credential, and both treat it accordingly.
/// </para>
/// <para>
/// An invitation is created even when the email fails to send. The account is
/// real either way, and a SuperAdmin can resend; discarding it would leave a
/// half-finished import that nobody can see or repair.
/// </para>
/// </remarks>
public sealed class InvitationService(
    IUserRepository users,
    IPharmacyInvitationRepository invitations,
    IPasswordHasher hasher,
    IEmailSender email,
    IAdminAuditRepository audit,
    IInvitationLinkBuilder links,
    ILogger<InvitationService> log)
{
    /// <summary>
    /// 72 hours. An invitation sent on Friday still works on Monday; an email
    /// forwarded round an office a fortnight later does not.
    /// </summary>
    private static readonly TimeSpan ValidFor = TimeSpan.FromHours(72);

    /// <summary>Guards a bulk import against a pasted spreadsheet of the wrong thing.</summary>
    private const int MaxBulkRows = 500;

    /* ---------------------------------------------------------------- *
     * Creating
     * ---------------------------------------------------------------- */

    public async Task<Result<InviteResultDto>> InviteAsync(
        AdminContext actor, InvitePharmacistRequest request, CancellationToken ct)
    {
        if (!actor.IsSuperAdmin)
            return Result<InviteResultDto>.Forbidden("Only a SuperAdmin can invite pharmacists.");

        var result = await InviteOneAsync(actor, request, ct);

        return result.Success
            ? Result<InviteResultDto>.Success(result)
            : Result<InviteResultDto>.Invalid(result.Error ?? "Could not invite this pharmacist.");
    }

    public async Task<Result<BulkInviteResultDto>> InviteManyAsync(
        AdminContext actor, BulkInviteRequest request, CancellationToken ct)
    {
        if (!actor.IsSuperAdmin)
            return Result<BulkInviteResultDto>.Forbidden("Only a SuperAdmin can invite pharmacists.");

        var rows = request.Pharmacists ?? Array.Empty<InvitePharmacistRequest>();
        if (rows.Count == 0)
            return Result<BulkInviteResultDto>.Invalid("There is nobody to invite.");
        if (rows.Count > MaxBulkRows)
            return Result<BulkInviteResultDto>.Invalid(
                $"That is {rows.Count} rows. Import at most {MaxBulkRows} at a time.");

        var results = new List<InviteResultDto>(rows.Count);

        // Sequential, not parallel. Duplicate detection reads the same
        // collection each row writes to, and the email provider rate-limits —
        // a burst of 500 concurrent sends is how an account gets throttled
        // halfway through an import.
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await InviteOneAsync(actor, row, ct));
        }

        var invited = results.Count(r => r.Success);

        await audit.AddAsync(
            AdminAuditEntry.Record(
                actor.UserId, actor.Email, AdminAction.InvitePharmacist,
                null, null,
                $"Bulk import: {invited} invited, {results.Count - invited} failed"),
            ct);

        return Result<BulkInviteResultDto>.Success(new BulkInviteResultDto(
            invited,
            results.Count - invited,
            results.Count(r => r.EmailSent),
            results));
    }

    /// <summary>
    /// Creates an invited account without checking a platform role.
    /// </summary>
    /// <remarks>
    /// For callers that have already established their own authority — the
    /// hierarchy service, where a parent pharmacy may invite its own
    /// sub-pharmacy. Deliberately narrow: it takes the inviter's email rather
    /// than an AdminContext, so it cannot be mistaken for the SuperAdmin path
    /// and cannot be used to grant anything.
    /// </remarks>
    public Task<InviteResultDto> InviteAccountAsync(
        InvitePharmacistRequest request, string invitedById, string invitedByEmail, CancellationToken ct) =>
        // A real user id, not the email. The invitation records who sent it,
        // and an audit trail keyed on an email address stops being traceable
        // the moment somebody changes theirs.
        InviteOneAsync(new AdminContext(invitedById, invitedByEmail, UserRole.PharmacyUser), request, ct);

    /// <summary>
    /// Creates one account and sends its invitation.
    /// </summary>
    /// <remarks>
    /// Never throws for a bad row — a batch must survive one malformed entry.
    /// Failures come back as an unsuccessful result the caller reports.
    /// </remarks>
    private async Task<InviteResultDto> InviteOneAsync(
        AdminContext actor, InvitePharmacistRequest request, CancellationToken ct)
    {
        var address = (request.Email ?? string.Empty).Trim();

        Email parsed;
        try { parsed = Email.Create(address); }
        catch (DomainException ex)
        {
            return new InviteResultDto(address, false, null, ex.Message, false);
        }

        if (string.IsNullOrWhiteSpace(request.FullName))
            return new InviteResultDto(address, false, null, "A full name is required.", false);

        var existing = await users.FindByEmailAsync(parsed, ct);
        if (existing is not null)
        {
            // Re-inviting an account that is still pending is a resend, which is
            // what someone re-running a partly-failed import actually wants.
            // Re-inviting an active account is refused, because it would look
            // like a way to reset somebody's password.
            if (existing.Status == AccountStatus.Invited)
            {
                var (resent, _) = await SendInvitationAsync(actor, existing, ct);
                return new InviteResultDto(
                    address, true, existing.Id,
                    resent ? null : "Account exists; the email could not be sent.", resent);
            }

            return new InviteResultDto(
                address, false, null, "An active account already uses this email.", false);
        }

        User user;
        try
        {
            // A random unusable hash rather than an empty one — see User.Invite.
            var placeholder = PasswordHash.FromHash(
                hasher.Hash(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")));

            user = User.Invite(parsed, request.FullName.Trim(), placeholder);

            var profile = BuildProfile(request);
            if (profile is not null) user.SetProfile(profile);
        }
        catch (DomainException ex)
        {
            return new InviteResultDto(address, false, null, ex.Message, false);
        }

        await users.AddAsync(user, ct);

        var (sent, _) = await SendInvitationAsync(actor, user, ct);

        return new InviteResultDto(
            address, true, user.Id,
            sent ? null : "Account created, but the invitation email could not be sent.", sent);
    }

    /// <summary>
    /// Builds a pharmacy profile from the supplied fields, or null when there
    /// is nothing to record.
    /// </summary>
    /// <remarks>
    /// <c>PharmacyProfile.Create</c> demands a name, a contact and a phone,
    /// which a minimal CSV row will not have. Rather than reject the row —
    /// the pharmacist completes onboarding themselves anyway — the profile is
    /// filled with what was given and left for them to finish.
    /// </remarks>
    private static PharmacyProfile? BuildProfile(InvitePharmacistRequest request)
    {
        var pharmacyName = Trimmed(request.PharmacyName) ?? Trimmed(request.FullName);
        if (pharmacyName is null) return null;

        return new PharmacyProfile(
            PharmacyName: pharmacyName,
            Address: Trimmed(request.Address) ?? string.Empty,
            PostCode: (Trimmed(request.PostCode) ?? string.Empty).ToUpperInvariant(),
            Description: string.Empty,
            ContactName: Trimmed(request.FullName) ?? string.Empty,
            ContactPhone: Trimmed(request.Phone) ?? string.Empty,
            SonarFCode: Trimmed(request.FCode)?.ToUpperInvariant(),
            // False on purpose: the pharmacist still walks through onboarding
            // after activating, and marking it complete here would skip the
            // step that collects everything the CSV did not carry.
            OnboardingCompleted: false);
    }

    /// <summary>
    /// Issues a fresh token, retires any outstanding ones, and emails the link.
    /// </summary>
    /// <returns>
    /// Whether the provider accepted the email, and the link that was sent.
    /// The link is returned so a Development-only diagnostic can complete an
    /// activation without a mailbox; nothing in production reads it.
    /// </returns>
    private Task<(bool EmailSent, string Link)> SendInvitationAsync(
        AdminContext actor, User user, CancellationToken ct) =>
        SendInvitationAsync(actor.Email, user, ct);

    private async Task<(bool EmailSent, string Link)> SendInvitationAsync(
        string issuedBy, User user, CancellationToken ct)
    {
        // Retire first. Leaving older links live would mean resending does not
        // revoke anything, so a leaked earlier email would still work.
        foreach (var previous in await invitations.ListForUserAsync(user.Id, ct))
        {
            if (previous.AcceptedAtUtc is not null || previous.IsSuperseded) continue;
            previous.Supersede();
            await invitations.UpdateAsync(previous, ct);
        }

        var (invitation, rawToken) = PharmacyInvitation.Issue(user.Id, issuedBy, ValidFor);
        await invitations.AddAsync(invitation, ct);

        var link = links.BuildActivationLink(rawToken);

        try
        {
            var result = await email.SendAsync(
                InvitationEmail.Build(user, link, invitation.ExpiresAtUtc), ct);

            if (result.Sent > 0) return (true, link);

            log.LogWarning(
                "Invitation email to {Email} was not accepted: {Note}", PersonalData.Mask(user.Email.Value), result.Note);
            return (false, link);
        }
        catch (Exception ex)
        {
            // The account and invitation survive a send failure — a SuperAdmin
            // can resend. Throwing here would abandon a created account with a
            // live token and no way to reach the person it belongs to.
            log.LogError(ex, "Sending the invitation email to {Email} failed", PersonalData.Mask(user.Email.Value));
            return (false, link);
        }
    }

    /// <summary>
    /// Reissues an invitation and returns the activation link itself.
    /// </summary>
    /// <remarks>
    /// Exists so the end-to-end test can actually complete an activation.
    /// Without it the suite can only check that an invitation was created and
    /// that a bad token is refused — it can never prove the one thing that
    /// matters, that a real link opens the account.
    /// <para>
    /// Handing out a live activation link is exactly as sensitive as it sounds,
    /// so the caller is responsible for refusing outside Development.
    /// <see cref="InvitationsController"/> gates it on the host environment,
    /// the same way <c>/api/diagnostics/jwt</c> does. Reissuing also supersedes
    /// any outstanding token, so using this cannot leave two live links.
    /// </para>
    /// </remarks>
    public async Task<Result<string>> ReissueLinkAsync(
        AdminContext actor, string userId, CancellationToken ct)
    {
        if (!actor.IsSuperAdmin)
            return Result<string>.Forbidden("Only a SuperAdmin can reissue an invitation.");

        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<string>.NotFound("Account not found.");

        if (user.Status == AccountStatus.Active)
            return Result<string>.Invalid("That account is already activated.");

        var (_, link) = await SendInvitationAsync(actor, user, ct);
        return Result<string>.Success(link);
    }

    /// <summary>
    /// Sends a fresh activation link to an invited account that asked to reset
    /// its password.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An imported or invited pharmacist has no password yet, so "forgot my
    /// password" is the natural thing for them to try — and it used to send
    /// nothing, silently, because the account cannot sign in. From the outside
    /// that is indistinguishable from email being broken, which is how it was
    /// reported from production.
    /// </para>
    /// <para>
    /// The link goes only to the account's own address, exactly as the reset
    /// would have, and the caller's answer is unchanged, so this reveals nothing
    /// a reset did not. Older links are superseded as with any reissue.
    /// </para>
    /// </remarks>
    /// <returns>Whether the provider accepted the email; null when the account is not awaiting activation.</returns>
    public async Task<bool?> ResendOnRequestAsync(User user, CancellationToken ct)
    {
        if (user.Status != AccountStatus.Invited || !user.IsActive) return null;

        // Keep whoever originally invited them. The invitations list shows
        // "invited by", and a reissue the pharmacist triggered should not
        // replace the person who actually added the account.
        var issuedBy = (await invitations.ListForUserAsync(user.Id, ct))
            .OrderByDescending(i => i.CreatedAtUtc)
            .Select(i => i.InvitedByEmail)
            .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e))
            ?? "self-service";

        var (sent, _) = await SendInvitationAsync(issuedBy, user, ct);
        log.LogInformation(
            "Activation link resent to invited account {Email} after a password reset request ({Outcome})",
            PersonalData.Mask(user.Email.Value), sent ? "accepted" : "not accepted");
        return sent;
    }

    /// <summary>Reissues an invitation for an account that has not activated.</summary>
    public async Task<Result<InviteResultDto>> ResendAsync(
        AdminContext actor, string userId, CancellationToken ct)
    {
        if (!actor.IsSuperAdmin)
            return Result<InviteResultDto>.Forbidden("Only a SuperAdmin can resend invitations.");

        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<InviteResultDto>.NotFound("Account not found.");

        if (user.Status == AccountStatus.Active)
            return Result<InviteResultDto>.Invalid(
                "That account is already activated. Ask them to use the forgotten-password flow instead.");

        var (sent, _) = await SendInvitationAsync(actor, user, ct);

        await audit.AddAsync(
            AdminAuditEntry.Record(
                actor.UserId, actor.Email, AdminAction.InvitePharmacist,
                user.Id, user.Email.Value,
                sent ? "Invitation resent" : "Invitation reissued, email failed"),
            ct);

        return Result<InviteResultDto>.Success(
            new InviteResultDto(user.Email.Value, true, user.Id,
                sent ? null : "The email could not be sent.", sent));
    }

    /* ---------------------------------------------------------------- *
     * Claiming — anonymous
     * ---------------------------------------------------------------- */

    /// <summary>Confirms a token is live and says whose account it opens.</summary>
    public async Task<Result<InvitationPreviewDto>> PreviewAsync(string token, CancellationToken ct)
    {
        var (invitation, user, failure) = await ResolveAsync(token, ct);
        if (failure is not null) return Result<InvitationPreviewDto>.Invalid(failure);

        return Result<InvitationPreviewDto>.Success(new InvitationPreviewDto(
            user!.Email.Value,
            user.DisplayName,
            user.Profile.PharmacyName,
            invitation!.ExpiresAtUtc));
    }

    /// <summary>Sets the password and opens the account.</summary>
    public async Task<Result<bool>> AcceptAsync(
        AcceptInvitationRequest request, CancellationToken ct)
    {
        var (invitation, user, failure) = await ResolveAsync(request.Token, ct);
        if (failure is not null) return Result<bool>.Invalid(failure);

        if (PasswordPolicy.WhyRejected(request.Password, user?.Email.Value) is { } weak)
            return Result<bool>.Invalid(weak);

        try
        {
            user!.Activate(PasswordHash.FromHash(hasher.Hash(request.Password)));
        }
        catch (DomainException ex)
        {
            return Result<bool>.Invalid(ex.Message);
        }

        // The invitation is consumed before the account is saved. If the second
        // write failed, a spent token would be left unusable — which is the
        // safe direction. The reverse would leave a live token on an activated
        // account, letting anyone with the link reset the password.
        invitation!.MarkAccepted();
        await invitations.UpdateAsync(invitation, ct);
        await users.UpdateAsync(user, ct);

        log.LogInformation("Account {Email} activated from an invitation", PersonalData.Mask(user.Email.Value));
        return Result<bool>.Success(true);
    }

    /// <summary>
    /// Resolves a raw token to its invitation and account.
    /// </summary>
    /// <remarks>
    /// Every failure returns the same message. Distinguishing "no such token"
    /// from "expired" from "already used" would let someone with a list of
    /// guessed links learn which ones exist.
    /// </remarks>
    private async Task<(PharmacyInvitation?, User?, string?)> ResolveAsync(
        string? token, CancellationToken ct)
    {
        const string Rejected =
            "This activation link is not valid. It may have expired or already been used — "
            + "ask an administrator to send a new one.";

        if (string.IsNullOrWhiteSpace(token)) return (null, null, Rejected);

        var invitation = await invitations.FindByTokenHashAsync(
            PharmacyInvitation.HashToken(token.Trim()), ct);

        if (invitation is null || !invitation.IsRedeemable(DateTime.UtcNow))
            return (null, null, Rejected);

        var user = await users.FindByIdAsync(invitation.UserId, ct);
        if (user is null || !user.IsActive) return (null, null, Rejected);

        return (invitation, user, null);
    }

    /* ---------------------------------------------------------------- *
     * Listing
     * ---------------------------------------------------------------- */

    public async Task<Result<InvitedListDto>> ListAsync(
        AdminContext actor, string? search, InvitationStatus? status,
        int skip, int take, CancellationToken ct)
    {
        if (!actor.IsSuperAdmin)
            return Result<InvitedListDto>.Forbidden("Only a SuperAdmin can view invitations.");

        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);

        var page = await users.SearchAsync(
            new UserQuery(Search: search, Role: UserRole.PharmacyUser, Skip: skip, Take: take), ct);

        var latest = await invitations.LatestForUsersAsync(
            page.Items.Select(u => u.Id).ToList(), ct);

        var items = new List<InvitedPharmacistDto>();

        foreach (var user in page.Items)
        {
            latest.TryGetValue(user.Id, out var invitation);

            // An account with no invitation at all self-registered. It is not
            // part of this list — showing it as "Activated" would imply an
            // invitation nobody sent.
            if (invitation is null) continue;

            var state = user.Status == AccountStatus.Active
                ? InvitationStatus.Activated
                : invitation.IsExpired(DateTime.UtcNow)
                    ? InvitationStatus.Expired
                    : InvitationStatus.Invited;

            if (status is { } wanted && state != wanted) continue;

            var history = await invitations.ListForUserAsync(user.Id, ct);

            items.Add(new InvitedPharmacistDto(
                user.Id,
                user.Email.Value,
                user.DisplayName,
                user.Profile.PharmacyName,
                user.Profile.SonarFCode,
                user.Profile.PostCode,
                state,
                invitation.CreatedAtUtc,
                invitation.ExpiresAtUtc,
                invitation.AcceptedAtUtc,
                invitation.InvitedByEmail,
                history.Count));
        }

        return Result<InvitedListDto>.Success(
            new InvitedListDto(items, items.Count, skip, take));
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
