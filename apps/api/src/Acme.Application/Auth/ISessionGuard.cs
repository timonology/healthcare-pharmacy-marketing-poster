namespace Acme.Application.Auth;

/// <summary>
/// Whether an access token's session is still current (security review R2-09).
/// </summary>
/// <remarks>
/// A signed token proves who it was issued to and when, not that the account
/// is still allowed in. Without this check, deactivating an account or
/// resetting a stolen password left every access token already handed out
/// working until it expired.
/// </remarks>
public interface ISessionGuard
{
    /// <summary>
    /// Null when the session is current; otherwise why not.
    /// </summary>
    /// <remarks>
    /// The rule depends on the account's part in the token:
    /// <list type="bullet">
    /// <item><see cref="SessionParty.SignedIn"/> — the person holding the
    /// credentials. The account must exist, be able to sign in, and not have
    /// ended its sessions after <paramref name="issuedAtUtc"/>.</item>
    /// <item><see cref="SessionParty.WorkedAs"/> — the pharmacy a delegated
    /// token acts as. It must exist and not be deactivated (the rule the switch
    /// applies when it mints the token), and must not have ended its sessions
    /// since. It need not be able to sign in itself: a head office runs
    /// branches that have never activated a login of their own.</item>
    /// </list>
    /// Ending a pharmacy's sessions therefore also ends anyone's session
    /// working as it. That covers a password reset at the branch too — the
    /// head office simply switches in again — and it is what stops a token
    /// issued before a deactivation reviving if the pharmacy is reactivated.
    /// </remarks>
    Task<string?> WhyEndedAsync(
        string userId, DateTime issuedAtUtc, SessionParty party, CancellationToken ct);

    /// <summary>
    /// Drops any cached state for the account, so a change made on this
    /// instance — a deactivation, a password reset — applies to the very next
    /// request rather than after the cache lapses. Call after the change is saved.
    /// </summary>
    void Forget(string userId);
}

/// <summary>Which part an account plays in an access token.</summary>
public enum SessionParty
{
    /// <summary>The person who signed in: the subject of an ordinary token, the actor of a delegated one.</summary>
    SignedIn,

    /// <summary>The pharmacy a delegated token is working as (its subject).</summary>
    WorkedAs,
}
