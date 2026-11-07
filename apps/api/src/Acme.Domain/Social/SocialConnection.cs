using Acme.Domain.Common;

namespace Acme.Domain.Social;

/// <summary>
/// One external account a pharmacy has connected, and the credentials for it.
/// </summary>
/// <remarks>
/// <para>
/// A pharmacy may hold several of these per platform — a group with three
/// Facebook Pages is ordinary, not an edge case — so the identity of a
/// connection is (owner, platform, external account id) rather than
/// (owner, platform).
/// </para>
/// <para>
/// Credentials live here as <see cref="EncryptedSecret"/> and are never
/// exposed: there is no property returning a plaintext token, and the DTO
/// layer has no field to put one in. Decryption happens in infrastructure, at
/// the moment of the outbound call, and the plaintext never travels back up.
/// </para>
/// <para>
/// Disconnecting sets <see cref="ConnectionStatus.Revoked"/> and clears the
/// secrets rather than deleting the row. Published history references the
/// connection, and a pharmacy asking "where did this post go?" six months
/// later deserves an answer better than a dangling id.
/// </para>
/// </remarks>
public sealed class SocialConnection : Entity
{
    public string OwnerId { get; private set; } = default!;
    public SocialPlatform Platform { get; private set; }

    /// <summary>
    /// The platform's own identifier: Page id, Instagram user id, GBP location
    /// name, or Google Ads customer id.
    /// </summary>
    public string ExternalAccountId { get; private set; } = default!;

    /// <summary>What the pharmacy will recognise — the Page or account name.</summary>
    public string DisplayName { get; private set; } = default!;

    /// <summary>Profile picture or logo, for the connection card. Optional.</summary>
    public string? AvatarUrl { get; private set; }

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Active;

    public EncryptedSecret? AccessToken { get; private set; }
    public EncryptedSecret? RefreshToken { get; private set; }

    /// <summary>
    /// When the access token stops working, if the platform said. Null means
    /// no stated expiry — Meta Page tokens derived from a long-lived user
    /// token are effectively non-expiring, which is not the same as safe to
    /// assume valid.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; private set; }

    public IReadOnlyList<string> Scopes { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// For Instagram: the Facebook Page this account hangs off. Meta issues no
    /// standalone Instagram credential, so publishing uses the Page's token and
    /// we have to know which Page that is.
    /// </summary>
    public string? ParentAccountId { get; private set; }

    /// <summary>
    /// The platform's id for the person who granted access — for Meta, the
    /// app-scoped Facebook user id.
    /// </summary>
    /// <remarks>
    /// Meta's deauthorize and data-deletion callbacks name the person, not the
    /// Page, so this is how those requests find what to revoke or delete.
    /// Null on connections made before it was recorded; reconnecting fills it.
    /// </remarks>
    public string? AuthorizedByUserId { get; private set; }

    /// <summary>
    /// SHA-256 of the refresh token, when there is one.
    /// </summary>
    /// <remarks>
    /// One Google sign-in backs one connection per Ads account, possibly in
    /// several pharmacies (a head office connecting a branch with its own
    /// login). Revoking that grant at Google breaks every one of them, so
    /// disconnecting only revokes when no other live connection carries the
    /// same fingerprint. A hash, not the token: refresh tokens are long and
    /// random, so it identifies without revealing, and it can be queried
    /// across pharmacies without decrypting anything.
    /// </remarks>
    public string? GrantFingerprint { get; private set; }

    public DateTime? LastVerifiedAtUtc { get; private set; }

    /// <summary>When the daily health check last looked at this connection.</summary>
    public DateTime? LastHealthCheckAtUtc { get; private set; }

    /// <summary>The platform's own words when it last refused us. Shown to the pharmacy.</summary>
    public string? LastError { get; private set; }

    private SocialConnection() { }

    public static SocialConnection Create(
        string ownerId,
        SocialPlatform platform,
        string externalAccountId,
        string displayName,
        EncryptedSecret accessToken,
        EncryptedSecret? refreshToken,
        DateTime? expiresAtUtc,
        IEnumerable<string>? scopes = null,
        string? parentAccountId = null,
        string? avatarUrl = null)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new DomainException("OwnerId is required.");
        if (string.IsNullOrWhiteSpace(externalAccountId))
            throw new DomainException("The platform did not return an account id.");
        if (string.IsNullOrWhiteSpace(displayName))
            throw new DomainException("The platform did not return an account name.");

        return new SocialConnection
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            Platform = platform,
            ExternalAccountId = externalAccountId.Trim(),
            DisplayName = displayName.Trim(),
            AvatarUrl = avatarUrl,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAtUtc = expiresAtUtc,
            Scopes = Normalize(scopes),
            ParentAccountId = parentAccountId,
            Status = ConnectionStatus.Active,
            LastVerifiedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Replaces the credentials after a re-authentication, reusing this record.
    /// </summary>
    /// <remarks>
    /// Reconnecting updates in place rather than creating a second row. The
    /// alternative leaves scheduled posts pointing at the dead connection while
    /// the pharmacy looks at a healthy-looking new one and wonders why nothing
    /// publishes.
    /// </remarks>
    public void UpdateCredentials(
        EncryptedSecret accessToken,
        EncryptedSecret? refreshToken,
        DateTime? expiresAtUtc,
        IEnumerable<string>? scopes = null)
    {
        AccessToken = accessToken;
        // Only overwrite the refresh token when a new one was issued. Google
        // returns one on the first consent and then omits it on subsequent
        // grants; blindly assigning null would discard the only copy we have
        // and turn a working connection into one that dies at the next expiry.
        if (refreshToken is not null) RefreshToken = refreshToken;
        ExpiresAtUtc = expiresAtUtc;
        if (scopes is not null) Scopes = Normalize(scopes);

        Status = ConnectionStatus.Active;
        LastError = null;
        LastVerifiedAtUtc = DateTime.UtcNow;
        Touch();
    }

    /// <summary>
    /// Takes a credential the platform rotated while it was being used.
    /// Leaves status and errors alone: a renewal says nothing about whether
    /// the post it was for went through.
    /// </summary>
    public void RenewCredential(EncryptedSecret accessToken, DateTime? expiresAtUtc)
    {
        if (Status == ConnectionStatus.Revoked) return;
        AccessToken = accessToken;
        ExpiresAtUtc = expiresAtUtc;
        Touch();
    }

    public void UpdateProfile(string displayName, string? avatarUrl)
    {
        if (!string.IsNullOrWhiteSpace(displayName)) DisplayName = displayName.Trim();
        if (avatarUrl is not null) AvatarUrl = avatarUrl;
        Touch();
    }

    /// <summary>
    /// The account this one is reached through — for Instagram, its Page.
    /// </summary>
    /// <remarks>
    /// Only ever replaced with a value, never cleared: a provider that omits
    /// it on reconnect has not told us the link is gone.
    /// </remarks>
    /// <summary>Records which grant this is. Only replaced with a value — a reconnect without a new refresh token keeps the old grant.</summary>
    public void UpdateGrantFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint == GrantFingerprint) return;
        GrantFingerprint = fingerprint;
        Touch();
    }

    /// <summary>Records who granted access. Only ever set, never cleared.</summary>
    public void UpdateAuthorizer(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId == AuthorizedByUserId) return;
        AuthorizedByUserId = userId;
        Touch();
    }

    public void UpdateParent(string? parentAccountId)
    {
        if (string.IsNullOrWhiteSpace(parentAccountId) || parentAccountId == ParentAccountId) return;
        ParentAccountId = parentAccountId;
        Touch();
    }

    /// <summary>Records that the platform rejected our credentials.</summary>
    public void MarkNeedsReauth(string reason)
    {
        Status = ConnectionStatus.NeedsReauth;
        LastError = Truncate(reason, 500);
        Touch();
    }

    public void MarkVerified()
    {
        Status = ConnectionStatus.Active;
        LastError = null;
        LastVerifiedAtUtc = DateTime.UtcNow;
        Touch();
    }

    /// <summary>
    /// Disconnects, destroying the stored credentials but keeping the record.
    /// </summary>
    public void Revoke()
    {
        Status = ConnectionStatus.Revoked;
        AccessToken = null;
        RefreshToken = null;
        ExpiresAtUtc = null;
        Touch();
    }

    /// <summary>
    /// True when this connection can be used for an outbound call right now.
    /// </summary>
    /// <remarks>
    /// The five-minute skew is not politeness. A token that expires during the
    /// call fails halfway through a multi-step Instagram publish — container
    /// created, publish refused — which leaves an orphaned draft on Meta's side
    /// that we cannot clean up. Better to force a re-auth slightly early.
    /// </remarks>
    public bool IsUsable(DateTime nowUtc) =>
        Status == ConnectionStatus.Active
        && AccessToken is not null
        && (ExpiresAtUtc is null || ExpiresAtUtc > nowUtc.AddMinutes(5));

    private static IReadOnlyList<string> Normalize(IEnumerable<string>? scopes) =>
        (scopes ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : s.Length <= max ? s : s[..max];
}
