using Acme.Domain.Users;

namespace Acme.Application.Auth;

/// <summary>
/// Storage for password-reset and email-verification tokens.
/// </summary>
/// <remarks>
/// Lookups are by hash, never by raw value — the raw token exists only in the
/// email that carries it. See <see cref="AuthToken"/>.
/// </remarks>
public interface IAuthTokenRepository
{
    Task AddAsync(AuthToken token, CancellationToken ct);

    Task UpdateAsync(AuthToken token, CancellationToken ct);

    /// <summary>Finds a token by the hash of the presented value.</summary>
    Task<AuthToken?> FindByTokenHashAsync(string tokenHash, CancellationToken ct);

    /// <summary>
    /// Every outstanding token of one purpose for an account.
    /// </summary>
    /// <remarks>
    /// Used to supersede older links when a new one is issued, so requesting
    /// two resets does not leave two working links in two inboxes.
    /// </remarks>
    Task<IReadOnlyList<AuthToken>> ListOutstandingAsync(
        string userId, AuthTokenPurpose purpose, CancellationToken ct);

    /// <summary>The most recently issued token of one purpose for an account, used or not.</summary>
    Task<AuthToken?> FindLatestAsync(string userId, AuthTokenPurpose purpose, CancellationToken ct);
}

/// <summary>
/// Builds the URLs that account emails point at.
/// </summary>
/// <remarks>
/// An abstraction for the same reason <c>IInvitationLinkBuilder</c> is one: the
/// application layer must not know the web app's address. Hardcoding it is how
/// a reset link emailed from the QA environment lands a tester on production.
/// </remarks>
public interface IAuthLinkBuilder
{
    string BuildPasswordResetLink(string rawToken);

    string BuildEmailVerificationLink(string rawToken);

    /// <summary>Where a welcome email sends someone who just signed up.</summary>
    string BuildAppLink(string path = "/dashboard");
}
