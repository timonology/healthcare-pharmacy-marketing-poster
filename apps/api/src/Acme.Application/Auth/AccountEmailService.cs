using Acme.Application.Audit;
using Acme.Application.Common;
using Acme.Application.Messaging;
using Acme.Domain.Common;
using Acme.Domain.Users;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Application.Auth;

/// <summary>
/// Account mail and the flows behind it: welcome, verify, reset.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="AuthService"/> so that signing in does not depend
/// on an email sender, a link builder and a token repository it never uses.
/// It reaches <c>IRefreshTokenRepository</c> directly to end sessions rather
/// than calling back into <c>AuthService</c>, which would be a cycle.
/// </para>
/// <para>
/// <b>Sending never fails a flow.</b> Registration completes whether or not the
/// welcome email goes out; a reset request reports success whether or not the
/// provider accepted it. The alternative is worse in both directions — a signup
/// that 500s because Mailjet is briefly down, and a reset endpoint whose
/// failures tell an attacker which addresses exist.
/// </para>
/// </remarks>
public sealed class AccountEmailService(
    IUserRepository users,
    IAuthTokenRepository tokens,
    IRefreshTokenRepository refreshTokens,
    IEmailSender email,
    IAuthLinkBuilder links,
    IPasswordHasher hasher,
    IOptions<AuthOptions> options,
    IAuditScope audit,
    ILogger<AccountEmailService> log,
    ILoginThrottle throttle,
    ISessionGuard sessions,
    Invitations.InvitationService invitations)
{
    private AuthOptions Options => options.Value;

    /* ── welcome ─────────────────────────────────────────────────────── */

    /// <summary>
    /// Sends the welcome email. Never throws.
    /// </summary>
    /// <remarks>
    /// Called from the registration path, and deliberately swallowing
    /// everything. A person who has just handed over their details and seen a
    /// spinner turn into an error will assume the account was not created and
    /// try again — producing a duplicate-email conflict on a second attempt,
    /// which is a far worse first impression than a missing welcome email.
    /// </remarks>
    public async Task SendWelcomeAsync(User user, CancellationToken ct)
    {
        if (!Options.SendWelcomeEmail) return;

        try
        {
            var result = await email.SendAsync(AuthEmails.Welcome(user, links.BuildAppLink()), ct);

            if (result.Sent > 0)
                log.LogInformation("Welcome email sent to {Email}", PersonalData.Mask(user.Email.Value));
            else
                log.LogWarning(
                    "Welcome email to {Email} was not accepted: {Note}", PersonalData.Mask(user.Email.Value), result.Note);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Sending the welcome email to {Email} failed", PersonalData.Mask(user.Email.Value));
        }
    }

    /* ── email verification ──────────────────────────────────────────── */

    /// <summary>Issues a verification link and emails it. Never throws.</summary>
    public async Task SendVerificationAsync(User user, CancellationToken ct)
    {
        if (user.EmailVerifiedAtUtc is not null) return;

        try
        {
            var token = await IssueAsync(user, AuthTokenPurpose.EmailVerification,
                TimeSpan.FromHours(Options.EmailVerificationHours), ct);

            var result = await email.SendAsync(
                AuthEmails.VerifyEmail(user, links.BuildEmailVerificationLink(token.Raw),
                    token.ExpiresAtUtc), ct);

            // The result used to be discarded, so a rejected verification
            // email left no trace anywhere.
            await RecordOutcomeAsync(token.Entity, OutcomeOf(result), ct);
            if (result.Sent == 0)
                log.LogWarning(
                    "Verification email to {Email} was not accepted: {Note}",
                    PersonalData.Mask(user.Email.Value), result.Note);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Sending the verification email to {Email} failed", PersonalData.Mask(user.Email.Value));
        }
    }

    /// <summary>
    /// Sends a fresh verification link to a signed-in account.
    /// </summary>
    /// <remarks>
    /// Silent about the outcome, like the reset request. There is no address to
    /// enumerate here — the identity comes from the session — but an account
    /// that is already verified should not be told "nothing to do" in a way
    /// that differs from success, because the caller is a page that should show
    /// the same reassuring message either way.
    /// </remarks>
    public async Task ResendVerificationAsync(string userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return;

        await SendVerificationAsync(user, ct);
    }

    /// <summary>Redeems a verification link.</summary>
    public async Task<Result<bool>> VerifyEmailAsync(string rawToken, CancellationToken ct)
    {
        var (token, user, error) = await RedeemAsync(rawToken, AuthTokenPurpose.EmailVerification, ct);
        if (error is not null) return Result<bool>.Invalid(error);

        AuditAs(user!);
        user!.MarkEmailVerified();
        await users.UpdateAsync(user, ct);

        token!.MarkUsed();
        await tokens.UpdateAsync(token, ct);

        log.LogInformation("Email verified for {Email}", PersonalData.Mask(user.Email.Value));
        return Result<bool>.Success(true);
    }

    private void AuditAs(User user)
    {
        audit.SetActor(AuditActor.ForUser(user.Id, user.Email.Value, user.Role));
        audit.SetEntity("user", user.Id);
    }

    /* ── password reset ──────────────────────────────────────────────── */

    /// <summary>
    /// Starts a reset. Reports success whether or not the address exists.
    /// </summary>
    /// <remarks>
    /// <b>The uniform response is the security property.</b> An endpoint that
    /// says "no account with that address" is an account-enumeration oracle:
    /// feed it a breach dump and it sorts the list into customers and
    /// non-customers, which is exactly what a credential-stuffing run wants.
    /// So every call returns the same thing, and the only difference is whether
    /// an email is actually sent.
    ///
    /// This is also why the method returns no indication of what happened —
    /// there is nothing for a caller to accidentally surface.
    /// </remarks>
    public async Task RequestPasswordResetAsync(string emailAddress, CancellationToken ct)
    {
        Email parsed;
        try { parsed = Email.Create(emailAddress); }
        catch (DomainException)
        {
            log.LogInformation("Password reset requested for an unparseable address");
            return;
        }

        var user = await users.FindByEmailAsync(parsed, ct);

        if (user is null)
        {
            // Logged, because "nobody is receiving reset emails" and "that
            // address was never registered" look identical from support.
            log.LogInformation(
                "Password reset requested for {Email}, which has no account", PersonalData.Mask(parsed.Value));
            return;
        }

        // Recorded against the account (the audit log is staff-only, so this
        // is no enumeration oracle): a burst of reset requests for one person
        // is worth seeing.
        AuditAs(user);

        // Invited and never activated — an imported pharmacy, a new branch. They
        // have no password to reset, so "forgot password" is the obvious thing
        // for them to try, and it used to send nothing at all. They get a fresh
        // activation link instead, to the same address.
        if (user.Status == AccountStatus.Invited && user.IsActive)
        {
            try
            {
                var resent = await invitations.ResendOnRequestAsync(user, ct);
                audit.AddMeta("delivery", resent == true ? "activation_link_accepted" : "activation_link_not_accepted");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Resending the activation link to {Email} failed", PersonalData.Mask(user.Email.Value));
                audit.AddMeta("delivery", "activation_link_error");
            }
            return;
        }

        if (!user.CanSignIn)
        {
            // A deactivated account gets no link. Resetting the password would
            // not let them in, and sending one implies it would.
            log.LogInformation(
                "Password reset requested for {Email}, which cannot sign in", PersonalData.Mask(parsed.Value));
            audit.AddMeta("delivery", "skipped_cannot_sign_in");
            return;
        }

        AuthToken? issued = null;
        try
        {
            var token = await IssueAsync(user, AuthTokenPurpose.PasswordReset,
                TimeSpan.FromMinutes(Options.PasswordResetMinutes), ct);
            issued = token.Entity;

            var result = await email.SendAsync(
                AuthEmails.PasswordReset(user, links.BuildPasswordResetLink(token.Raw),
                    token.ExpiresAtUtc), ct);

            var outcome = OutcomeOf(result);
            await RecordOutcomeAsync(issued, outcome, ct);
            audit.AddMeta("delivery", outcome);

            if (result.Sent > 0)
                log.LogInformation("Password reset email sent to {Email}", PersonalData.Mask(user.Email.Value));
            else
                log.LogWarning(
                    "Password reset email to {Email} was not accepted: {Note}",
                    PersonalData.Mask(user.Email.Value), result.Note);
        }
        catch (Exception ex)
        {
            // Swallowed on purpose: a 500 here is an enumeration oracle just as
            // surely as a "no such account" message, because it only happens
            // for addresses that exist.
            log.LogError(ex, "Sending the password reset email to {Email} failed", PersonalData.Mask(user.Email.Value));
            audit.AddMeta("delivery", AuthToken.EmailError);
            if (issued is not null) await RecordOutcomeAsync(issued, AuthToken.EmailError, ct);
        }
    }

    /// <summary>
    /// What happened to the last account email of one kind sent to this account.
    /// </summary>
    /// <remarks>
    /// For the account itself, signed in — there is no address to probe, so the
    /// uniform answer the anonymous request gives is not weakened. It turns
    /// "the reset email never came" from a guess into a fact, for support and for
    /// the smoke test, which requests a reset for its own account and then asks.
    /// </remarks>
    public async Task<AccountEmailStatusDto?> LatestAsync(
        string userId, AuthTokenPurpose purpose, CancellationToken ct)
    {
        var token = await tokens.FindLatestAsync(userId, purpose, ct);
        if (token is null) return null;

        string? linkHost = null;
        try
        {
            var sample = purpose == AuthTokenPurpose.PasswordReset
                ? links.BuildPasswordResetLink("sample")
                : links.BuildEmailVerificationLink("sample");
            linkHost = new Uri(sample).Host;
        }
        catch (UriFormatException) { /* reported as null: the link would not work */ }

        return new AccountEmailStatusDto(
            purpose.ToString(), token.CreatedAtUtc, token.EmailOutcome ?? "unknown", linkHost);
    }

    private static string OutcomeOf(EmailDispatchResult result) =>
        result.Mocked ? AuthToken.EmailMocked
        : result.Sent > 0 ? AuthToken.EmailAccepted
        : AuthToken.EmailRejected;

    /// <summary>Best-effort: a failure to record must not change the flow's answer.</summary>
    private async Task RecordOutcomeAsync(AuthToken token, string outcome, CancellationToken ct)
    {
        try
        {
            token.RecordEmailOutcome(outcome);
            await tokens.UpdateAsync(token, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not record the email outcome on token {Token}", token.Id);
        }
    }

    /// <summary>
    /// Completes a reset: sets the new password and ends every session.
    /// </summary>
    public async Task<Result<bool>> ResetPasswordAsync(
        string rawToken, string newPassword, CancellationToken ct)
    {
        if (PasswordPolicy.WhyRejected(newPassword) is { } weak)
            return Result<bool>.Invalid(weak);

        var (token, user, error) = await RedeemAsync(rawToken, AuthTokenPurpose.PasswordReset, ct);
        if (error is not null) return Result<bool>.Invalid(error);

        AuditAs(user!);
        user!.ChangePassword(PasswordHash.FromHash(hasher.Hash(newPassword)));
        // Access tokens already issued stop working too, not just refresh
        // tokens below (R2-09).
        user.EndAllSessions();
        await users.UpdateAsync(user, ct);
        sessions.Forget(user.Id);

        token!.MarkUsed();
        await tokens.UpdateAsync(token, ct);

        // Every session, including whoever prompted the reset.
        //
        // The common reason to reset a password is believing somebody else has
        // it. Leaving existing refresh tokens alive would let that person keep
        // access for up to thirty days after the password they stole stopped
        // working — the reset would feel effective and change nothing.
        await refreshTokens.RevokeAllForUserAsync(user.Id, ct);

        // A reset proves ownership through the mailbox, so whatever sign-in
        // pause strangers caused on this account no longer applies (R2-08).
        throttle.Clear(user.Email.Value);

        log.LogInformation(
            "Password reset completed for {Email}; all sessions revoked", PersonalData.Mask(user.Email.Value));

        // Best-effort notification. If an intruder did this, the reset email
        // went to a mailbox they control or was deleted — this is the owner's
        // only signal, so a send failure must not undo the reset.
        try
        {
            await email.SendAsync(AuthEmails.PasswordChanged(user, DateTime.UtcNow), ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Sending the password-changed notice to {Email} failed", PersonalData.Mask(user.Email.Value));
        }

        return Result<bool>.Success(true);
    }

    /* ── shared ──────────────────────────────────────────────────────── */

    private async Task<(string Raw, DateTime ExpiresAtUtc, AuthToken Entity)> IssueAsync(
        User user, AuthTokenPurpose purpose, TimeSpan validFor, CancellationToken ct)
    {
        // Retire anything outstanding for this purpose first. Otherwise asking
        // twice leaves two working links, and the older email — the one more
        // likely to have been forwarded or intercepted — still opens the door.
        foreach (var old in await tokens.ListOutstandingAsync(user.Id, purpose, ct))
        {
            old.Supersede();
            await tokens.UpdateAsync(old, ct);
        }

        var (token, raw) = AuthToken.Issue(user.Id, user.Email.Value, purpose, validFor);
        await tokens.AddAsync(token, ct);

        return (raw, token.ExpiresAtUtc, token);
    }

    /// <summary>
    /// Looks up and validates a presented token.
    /// </summary>
    /// <remarks>
    /// One error message for every failure. Distinguishing "expired" from
    /// "already used" from "never existed" tells someone probing tokens which
    /// guesses were close, and none of the three changes what the person in
    /// front of the screen should do: ask for another link.
    /// </remarks>
    private async Task<(AuthToken? Token, User? User, string? Error)> RedeemAsync(
        string rawToken, AuthTokenPurpose purpose, CancellationToken ct)
    {
        const string Rejected = "This link is invalid or has expired. Please request a new one.";

        if (string.IsNullOrWhiteSpace(rawToken))
            return (null, null, Rejected);

        var token = await tokens.FindByTokenHashAsync(AuthToken.HashToken(rawToken), ct);

        if (token is null || token.Purpose != purpose || !token.IsRedeemable(DateTime.UtcNow))
            return (null, null, Rejected);

        var user = await users.FindByIdAsync(token.UserId, ct);
        if (user is null) return (null, null, Rejected);

        // The address must still be the one the link was sent to. Without this,
        // somebody who reaches an unlocked session, changes the email, and then
        // uses a reset link issued to the old address keeps a way back in after
        // the real owner recovers the account.
        if (!string.Equals(token.SentToEmail, user.Email.Value, StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning(
                "Token for {Sent} presented after the address changed to {Current}",
                PersonalData.Mask(token.SentToEmail), PersonalData.Mask(user.Email.Value));
            return (null, null, Rejected);
        }

        return (token, user, null);
    }
}
