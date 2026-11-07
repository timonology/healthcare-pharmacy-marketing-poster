using Acme.Application.Audit;
using Acme.Application.Common;
using Acme.Domain.Auth;
using Acme.Domain.Common;
using Acme.Domain.Users;
using Microsoft.Extensions.Options;

namespace Acme.Application.Auth;

public sealed class AuthService(
    IUserRepository users,
    IPasswordHasher hasher,
    IJwtTokenService jwt,
    IRefreshTokenRepository refreshTokens,
    IRefreshTokenGenerator refreshGenerator,
    AccountEmailService accountEmail,
    IAuditScope audit,
    ILoginThrottle throttle,
    IOptions<AuthOptions> options)
{
    /// <summary>
    /// A real hash of a random value, checked when the address is unknown so the
    /// refusal costs the same as a wrong password. Made once per process.
    /// </summary>
    private static string? s_timingHash;

    private string TimingHash() =>
        s_timingHash ??= hasher.Hash("timing-" + Guid.NewGuid().ToString("N"));

    private readonly TimeSpan _refreshLifetime = TimeSpan.FromDays(options.Value.RefreshTokenDays);

    public async Task<Result<AuthResponse>> RegisterAsync(RegisterRequest req, CancellationToken ct)
    {
        Email email;
        try { email = Email.Create(req.Email); }
        catch (DomainException ex) { return Result<AuthResponse>.Invalid(ex.Message); }

        if (PasswordPolicy.WhyRejected(req.Password, req.Email) is { } weak)
            return Result<AuthResponse>.Invalid(weak);

        if (await users.FindByEmailAsync(email, ct) is not null)
            return Result<AuthResponse>.Conflict("Email is already registered.");

        var hash = PasswordHash.FromHash(hasher.Hash(req.Password));

        User user;
        try { user = User.Register(email, req.DisplayName, hash); }
        catch (DomainException ex) { return Result<AuthResponse>.Invalid(ex.Message); }

        await users.AddAsync(user, ct);

        // Welcome and verification mail, in that order, and neither can fail
        // the registration. Both swallow their own exceptions — see
        // AccountEmailService — because somebody who has just submitted the
        // signup form and been shown an error will assume no account was
        // created and try again, hitting a duplicate-email conflict on the
        // second attempt. A missing welcome email is a far smaller problem.
        await accountEmail.SendWelcomeAsync(user, ct);
        await accountEmail.SendVerificationAsync(user, ct);

        AuditAs(user);
        return Result<AuthResponse>.Success(await IssueAsync(user, ct));
    }

    /// <param name="clientAddress">
    /// The caller's address as the API resolved it (signed by the web app, or
    /// the platform's own hop — see SignedClientIpMiddleware). Keys the
    /// per-address lock, so a stranger's failures do not lock the owner out.
    /// </param>
    public async Task<Result<AuthResponse>> LoginAsync(
        LoginRequest req, CancellationToken ct, string? clientAddress = null)
    {
        Email email;
        try { email = Email.Create(req.Email); }
        catch (DomainException) { return Result<AuthResponse>.Unauthorized("Invalid email or password."); }

        // Paused after repeated failures, for any account — known or not, so
        // the pause cannot be used to find out which accounts exist (SEC-05).
        // Keyed on account + address, so it pauses the guesser, not the owner (R2-08).
        if (throttle.IsLocked(email.Value, clientAddress))
        {
            audit.AddMeta("reason", "throttled");
            return Result<AuthResponse>.Unauthorized(
                "Too many sign-in attempts. Try again later, or reset your password to sign in now.");
        }

        var user = await users.FindByEmailAsync(email, ct);
        if (user is null)
        {
            // Hashed anyway, so an unknown address takes as long to refuse as a
            // wrong password and timing cannot tell them apart (SEC-12).
            hasher.Verify(req.Password ?? string.Empty, TimingHash());
            throttle.RecordFailure(email.Value, clientAddress);
            return Result<AuthResponse>.Unauthorized("Invalid email or password.");
        }

        // From here the attempt is against a real account, and is recorded
        // against it: "someone failed to sign in as X" is what a reviewer looks
        // for. An unknown address stays anonymous — the typed address is not
        // kept, since it is often a password typed into the wrong box.
        AuditAs(user);
        if (!hasher.Verify(req.Password, user.PasswordHash.Value))
        {
            throttle.RecordFailure(email.Value, clientAddress);
            audit.AddMeta("reason", "bad_password");
            return Result<AuthResponse>.Unauthorized("Invalid email or password.");
        }

        // Checked only after the password verifies. Reporting "account
        // deactivated" to someone who hasn't proven they own the account would
        // confirm the address exists.
        if (!user.IsActive)
        {
            audit.AddMeta("reason", "deactivated");
            return Result<AuthResponse>.Forbidden(
                "This account has been deactivated. Contact your administrator.");
        }

        // An invited account has an unguessable placeholder hash, so reaching
        // here at all should be impossible — the password check above cannot
        // pass. Stated anyway: this is the boundary the whole invitation flow
        // rests on, and "unreachable" is a claim about code that changes.
        if (user.Status == AccountStatus.Invited)
        {
            audit.AddMeta("reason", "not_activated");
            return Result<AuthResponse>.Forbidden(
                "This account has not been activated yet. Use the link in your invitation email, "
                + "or ask an administrator to send a new one.");
        }

        throttle.RecordSuccess(email.Value, clientAddress);
        user.RecordLogin();
        await users.UpdateAsync(user, ct);

        return Result<AuthResponse>.Success(await IssueAsync(user, ct));
    }

    public async Task<Result<AuthResponse>> RefreshAsync(string rawToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return Result<AuthResponse>.Unauthorized("Refresh token is required.");

        var hash = refreshGenerator.Hash(rawToken);
        var existing = await refreshTokens.FindByHashAsync(hash, ct);
        if (existing is null)
            return Result<AuthResponse>.Unauthorized("Refresh token not recognized.");

        if (!existing.IsActive)
        {
            await refreshTokens.RevokeAllForUserAsync(existing.UserId, ct);
            return Result<AuthResponse>.Unauthorized("Refresh token is no longer valid.");
        }

        var user = await users.FindByIdAsync(existing.UserId, ct);
        if (user is null) return Result<AuthResponse>.Unauthorized("Account not found.");

        // Deactivation must take effect promptly. Without this an already-issued
        // refresh token would keep minting fresh access tokens for a disabled
        // account until it expired — up to 30 days.
        //
        // CanSignIn covers both deactivation and a never-activated invitation.
        // Using it here rather than repeating the two checks is the point of
        // the property: this is exactly the path where a second condition
        // would have been forgotten.
        if (!user.CanSignIn)
        {
            await refreshTokens.RevokeAllForUserAsync(user.Id, ct);
            return Result<AuthResponse>.Forbidden("This account can no longer sign in.");
        }

        var (accessToken, accessExp) = jwt.Issue(user);
        var newRaw = refreshGenerator.Generate();
        var newHash = refreshGenerator.Hash(newRaw);
        var newToken = RefreshToken.Issue(user.Id, newHash, _refreshLifetime);
        await refreshTokens.AddAsync(newToken, ct);

        existing.Rotate(newHash);
        await refreshTokens.UpdateAsync(existing, ct);

        return Result<AuthResponse>.Success(new AuthResponse(
            accessToken,
            accessExp,
            newRaw,
            newToken.ExpiresAtUtc,
            new UserProfile(user.Id, user.Email.Value, user.DisplayName, user.Tier, user.Profile.OnboardingCompleted, user.CreatedAtUtc, user.Role)));
    }

    /// <summary>The sign-in paths have no token yet: name the account for the audit row.</summary>
    private void AuditAs(User user)
    {
        audit.SetActor(AuditActor.ForUser(user.Id, user.Email.Value, user.Role));
        audit.SetEntity("user", user.Id);
    }

    public async Task LogoutAsync(string userId, CancellationToken ct) =>
        await refreshTokens.RevokeAllForUserAsync(userId, ct);

    private async Task<AuthResponse> IssueAsync(User user, CancellationToken ct)
    {
        var (accessToken, accessExp) = jwt.Issue(user);

        var raw = refreshGenerator.Generate();
        var token = RefreshToken.Issue(user.Id, refreshGenerator.Hash(raw), _refreshLifetime);
        await refreshTokens.AddAsync(token, ct);

        return new AuthResponse(
            accessToken,
            accessExp,
            raw,
            token.ExpiresAtUtc,
            new UserProfile(user.Id, user.Email.Value, user.DisplayName, user.Tier, user.Profile.OnboardingCompleted, user.CreatedAtUtc, user.Role));
    }
}
