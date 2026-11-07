using System.Security.Cryptography;
using System.Text;
using Acme.Domain.Common;

namespace Acme.Domain.Users;

/// <summary>
/// A pending invitation for a pharmacist to claim an account.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a hash of the token is stored.</b> The raw value exists once, in the
/// email that carries it, and is never written down here. Storing it in plain
/// text would mean anyone with database access — a backup, a support query, a
/// leaked snapshot — could activate any pending account and set its password.
/// The same reasoning the refresh tokens in this codebase already follow.
/// </para>
/// <para>
/// Expiry is 72 hours. Long enough that an invitation sent on a Friday still
/// works on Monday morning; short enough that an old email forwarded around an
/// office is not a live credential. A SuperAdmin can reissue at any time, which
/// is what makes the short window acceptable.
/// </para>
/// </remarks>
public sealed class PharmacyInvitation : Entity
{
    /// <summary>The account this invitation activates.</summary>
    public string UserId { get; private set; } = default!;

    /// <summary>SHA-256 of the raw token, hex. The raw value is never persisted.</summary>
    public string TokenHash { get; private set; } = default!;

    public DateTime ExpiresAtUtc { get; private set; }

    /// <summary>Set when the pharmacist completes activation. Null while pending.</summary>
    public DateTime? AcceptedAtUtc { get; private set; }

    /// <summary>
    /// Superseded by a later invitation for the same account.
    /// </summary>
    /// <remarks>
    /// Resending does not delete the old row. Keeping it, marked, means "this
    /// link was reissued three times over two weeks" is answerable — which
    /// matters when a pharmacist says they never received anything.
    /// </remarks>
    public bool IsSuperseded { get; private set; }

    /// <summary>Who sent it. Shown in the admin list and recorded in the audit trail.</summary>
    public string InvitedByEmail { get; private set; } = default!;

    private PharmacyInvitation() { }

    /// <summary>
    /// Issues an invitation, returning the entity and the raw token to email.
    /// </summary>
    /// <remarks>
    /// The raw token is returned rather than exposed as a property, so there is
    /// no way to read it back later — not from the repository, not from a DTO,
    /// not by accident. If the email fails to send, the invitation must be
    /// reissued rather than recovered.
    /// </remarks>
    public static (PharmacyInvitation Invitation, string RawToken) Issue(
        string userId, string invitedByEmail, TimeSpan validFor)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new DomainException("An invitation needs an account.");

        // 32 bytes from a CSPRNG, base64url. Guessing is not a realistic
        // attack at this size, which is what lets the link stand alone as
        // proof of ownership of the mailbox.
        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));

        var invitation = new PharmacyInvitation
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            TokenHash = HashToken(raw),
            ExpiresAtUtc = DateTime.UtcNow.Add(validFor),
            InvitedByEmail = invitedByEmail,
        };

        return (invitation, raw);
    }

    /// <summary>SHA-256, hex. Deterministic so a presented token can be looked up directly.</summary>
    /// <remarks>
    /// A plain hash rather than a slow KDF, deliberately: the token is 256 bits
    /// of random data, not a human-chosen password, so there is no dictionary to
    /// attack and nothing for work factors to buy.
    /// </remarks>
    public static string HashToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

    public bool IsExpired(DateTime nowUtc) => ExpiresAtUtc <= nowUtc;

    /// <summary>True when this invitation can still be redeemed.</summary>
    public bool IsRedeemable(DateTime nowUtc) =>
        AcceptedAtUtc is null && !IsSuperseded && !IsExpired(nowUtc);

    public void MarkAccepted()
    {
        if (AcceptedAtUtc is not null)
            throw new DomainException("This invitation has already been used.");

        AcceptedAtUtc = DateTime.UtcNow;
        Touch();
    }

    /// <summary>
    /// Retires this invitation because a newer one was issued.
    /// </summary>
    /// <remarks>
    /// Called on every outstanding invitation when one is resent. Without it an
    /// older email would remain valid alongside the new one, so revoking access
    /// by resending would not actually revoke anything.
    /// </remarks>
    public void Supersede()
    {
        if (AcceptedAtUtc is not null) return;
        IsSuperseded = true;
        Touch();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
