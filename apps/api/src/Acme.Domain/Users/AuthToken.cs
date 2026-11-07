using System.Security.Cryptography;
using System.Text;
using Acme.Domain.Common;

namespace Acme.Domain.Users;

/// <summary>What a token entitles the holder to do.</summary>
public enum AuthTokenPurpose
{
    /// <summary>Set a new password without knowing the old one.</summary>
    PasswordReset = 0,

    /// <summary>Confirm that the address on the account can receive mail.</summary>
    EmailVerification = 1,
}

/// <summary>
/// A single-use, expiring proof that someone controls an account's mailbox.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <see cref="PharmacyInvitation"/>, which solved the same problem
/// first, and shares its two load-bearing decisions: <b>only a hash is
/// stored</b>, and the raw value is returned once from <see cref="Issue"/>
/// rather than exposed as a property, so it cannot be read back from the
/// database, a DTO, or a log line.
/// </para>
/// <para>
/// One entity with a <see cref="AuthTokenPurpose"/> rather than two near-identical
/// ones. The lifecycle is genuinely the same — issue, email, redeem once,
/// expire — and the purpose is checked on redemption, so a verification link
/// cannot be presented to the reset endpoint.
/// </para>
/// <para>
/// <b>Reset tokens are short-lived.</b> One hour, against 72 for an invitation.
/// An invitation is expected to sit in an inbox over a weekend; a reset is
/// requested by someone who is at the keyboard right now, and the window is
/// the period in which a forwarded or intercepted email is a live credential
/// for an existing account.
/// </para>
/// </remarks>
public sealed class AuthToken : Entity
{
    public string UserId { get; private set; } = default!;

    public AuthTokenPurpose Purpose { get; private set; }

    /// <summary>SHA-256 of the raw token, hex. The raw value is never persisted.</summary>
    public string TokenHash { get; private set; } = default!;

    public DateTime ExpiresAtUtc { get; private set; }

    /// <summary>Set when redeemed. Null while outstanding.</summary>
    public DateTime? UsedAtUtc { get; private set; }

    /// <summary>Retired because a newer token for the same purpose was issued.</summary>
    public bool IsSuperseded { get; private set; }

    /// <summary>
    /// The address the token was sent to.
    /// </summary>
    /// <remarks>
    /// Recorded so a reset issued before an address change cannot be redeemed
    /// after it — otherwise someone who reaches an unlocked session, changes
    /// the email, and then uses an older reset link keeps a way back in.
    /// </remarks>
    public string SentToEmail { get; private set; } = default!;

    /// <summary>
    /// What the email provider said about the email carrying this token:
    /// <see cref="EmailAccepted"/>, <see cref="EmailRejected"/>,
    /// <see cref="EmailError"/> or <see cref="EmailMocked"/>. Null until a send
    /// has been attempted.
    /// </summary>
    /// <remarks>
    /// A reset request answers the same thing whether or not anything was sent
    /// — that is what stops it being an enumeration oracle — so without this the
    /// only record of "did the email go?" was a log line. Kept on the token so
    /// support, and the smoke test acting as its own account, can answer it.
    /// </remarks>
    public string? EmailOutcome { get; private set; }

    public const string EmailAccepted = "accepted";
    public const string EmailRejected = "rejected";
    public const string EmailError = "error";

    /// <summary>Counted as sent, but the sender is in mock mode: nothing was delivered.</summary>
    public const string EmailMocked = "mocked";

    private AuthToken() { }

    /// <summary>
    /// Issues a token, returning the entity and the raw value to email.
    /// </summary>
    public static (AuthToken Token, string RawToken) Issue(
        string userId, string sentToEmail, AuthTokenPurpose purpose, TimeSpan validFor)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new DomainException("A token needs an account.");
        if (string.IsNullOrWhiteSpace(sentToEmail))
            throw new DomainException("A token needs an address to be sent to.");

        // 32 bytes from a CSPRNG, base64url — the same size the invitation
        // flow uses. At 256 bits the link stands alone as proof of mailbox
        // control, because guessing is not a realistic attack.
        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));

        var token = new AuthToken
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Purpose = purpose,
            TokenHash = HashToken(raw),
            SentToEmail = sentToEmail,
            ExpiresAtUtc = DateTime.UtcNow.Add(validFor),
        };

        return (token, raw);
    }

    /// <summary>SHA-256, hex. Deterministic so a presented token can be looked up directly.</summary>
    /// <remarks>
    /// A plain hash rather than a slow KDF, deliberately: this is 256 bits of
    /// random data, not a human-chosen password, so there is no dictionary to
    /// attack and nothing a work factor would buy.
    /// </remarks>
    public static string HashToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

    public bool IsExpired(DateTime nowUtc) => ExpiresAtUtc <= nowUtc;

    /// <summary>True when this token can still be redeemed.</summary>
    public bool IsRedeemable(DateTime nowUtc) =>
        UsedAtUtc is null && !IsSuperseded && !IsExpired(nowUtc);

    /// <summary>
    /// Marks the token spent.
    /// </summary>
    /// <remarks>
    /// Throws rather than returning false on a second call. A reset that
    /// quietly succeeds twice is a link that stays live in an inbox after the
    /// password has already been changed.
    /// </remarks>
    public void MarkUsed()
    {
        if (UsedAtUtc is not null)
            throw new DomainException("This link has already been used.");

        UsedAtUtc = DateTime.UtcNow;
        Touch();
    }

    /// <summary>
    /// Retires this token because a newer one was issued.
    /// </summary>
    /// <remarks>
    /// Called on every outstanding token of the same purpose when one is
    /// issued. Without it, requesting a reset twice would leave two live links,
    /// and the older email — the one more likely to have been intercepted —
    /// would still work.
    /// </remarks>
    public void Supersede()
    {
        if (UsedAtUtc is not null) return;
        IsSuperseded = true;
        Touch();
    }

    /// <summary>Records what the provider said about the email carrying this token.</summary>
    public void RecordEmailOutcome(string outcome)
    {
        if (outcome is not (EmailAccepted or EmailRejected or EmailError or EmailMocked))
            throw new DomainException($"Unknown email outcome '{outcome}'.");

        EmailOutcome = outcome;
        Touch();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
