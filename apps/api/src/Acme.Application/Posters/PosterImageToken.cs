using System.Security.Cryptography;
using System.Text;

namespace Acme.Application.Posters;

/// <summary>
/// A signed capability to read one rendered poster image, for the link in a
/// campaign email.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the poster is linked rather than embedded.</b> Mailjet's Send v3.1
/// puts inlined attachments inside <i>each</i> personalisation, so a poster
/// travels once per recipient. Measured on a real failure: 977,045 bytes for
/// four recipients, 244 KB each. That is 244 MB for a thousand-recipient
/// campaign, on any connection — and on the uplink this was debugged from, four
/// recipients took eighty seconds to upload and the provider closed the
/// connection before the request finished. Linking the image takes the poster
/// out of the request entirely: about 5 KB per recipient, and the same thousand
/// recipients move a few megabytes.
/// </para>
/// <para>
/// <b>The token binds the blob key, not the poster id.</b> Blob keys are
/// content-addressed — <c>PosterImageService.BuildKey</c> hashes the resolved
/// canvas — so a link in a sent email keeps showing what was sent even after
/// the poster is edited and re-rendered. Binding the poster id would mean
/// editing a design silently rewrote every campaign already in people's inboxes.
/// </para>
/// <para>
/// The blob key is also the whole of the address: there is no poster id in the
/// route. Nothing to enumerate, and no unsigned parameter for the endpoint to
/// have to distrust.
/// </para>
/// <para>
/// Signed with its own key derived from Signing:LinkSecret, like
/// <c>VideoDownloadToken</c> (security review R2-13; see LinkSigningKeys).
/// Not shared code with it, deliberately: that
/// class bakes its own payload shape, and changing it would invalidate the
/// 48-hour video links already sitting in recipients' inboxes. Worth unifying
/// the next time both are being touched anyway.
/// </para>
/// <para>
/// Token format: <c>base64url(payload) + "." + base64url(hmac)</c>, payload
/// <c>{blobKey}|{unix_expiry}</c>. Base64url is encoding, not encryption — the
/// blob key is readable by anyone holding the link, which is why it carries no
/// personal data.
/// </para>
/// </remarks>
public static class PosterImageToken
{
    /// <summary>
    /// How long a link in a campaign email keeps working.
    /// </summary>
    /// <remarks>
    /// Six months, where a video link gets 48 hours, because the two are not the
    /// same kind of thing. A video link is followed once, from a message just
    /// received. The poster is the message: somebody who opens the email in
    /// March to check when the flu clinic was should still see it. Long enough
    /// to be useful, bounded enough that a leaked link does not last forever,
    /// and revocable by rotating Signing:LinkSecret (and, for links signed
    /// before R2-13, the JWT secret).
    /// </remarks>
    public static readonly TimeSpan EmailLinkValidity = TimeSpan.FromDays(180);

    private static byte[]? _key;

    /// <summary>Called once at startup, from Program.cs.</summary>
    public static void Configure(string secret) => Configure(secret, legacySecret: secret);

    /// <summary>
    /// Signs with this link type's own key derived from <paramref name="linkSecret"/>,
    /// and still accepts links signed under <paramref name="legacySecret"/> (security review R2-13).
    /// </summary>
    public static void Configure(string linkSecret, string? legacySecret)
    {
        var (signing, accepted) = Common.LinkSigningKeys.For("poster-image", linkSecret, legacySecret);
        _key = signing;
        _accepted = accepted;
    }

    private static byte[][] _accepted = Array.Empty<byte[]>();

    public static string Sign(string blobKey, DateTimeOffset expiresAt)
    {
        var key = _key ?? throw new InvalidOperationException(
            "PosterImageToken.Configure must be called at startup.");

        var payloadBytes = Encoding.UTF8.GetBytes($"{blobKey}|{expiresAt.ToUnixTimeSeconds()}");
        using var mac = new HMACSHA256(key);
        return $"{ToBase64Url(payloadBytes)}.{ToBase64Url(mac.ComputeHash(payloadBytes))}";
    }

    /// <summary>Signed for the standard email window.</summary>
    public static string SignForEmail(string blobKey) =>
        Sign(blobKey, DateTimeOffset.UtcNow.Add(EmailLinkValidity));

    /// <summary>
    /// Reads the blob key back out of a token, or explains why it will not.
    /// </summary>
    /// <remarks>
    /// The key comes <i>from</i> the token rather than being checked against
    /// something the caller supplied. The signature is what makes it
    /// trustworthy, so there is nothing else to compare it to — and no way for a
    /// caller to ask for a blob the signer did not name.
    /// </remarks>
    public static bool TryReadBlobKey(string? token, out string blobKey, out string? reason)
    {
        blobKey = "";
        reason = null;

        var key = _key;
        if (key is null) { reason = "Server not configured."; return false; }
        if (string.IsNullOrWhiteSpace(token)) { reason = "Missing token."; return false; }

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) { reason = "Malformed token."; return false; }

        byte[] payloadBytes, sig;
        try
        {
            payloadBytes = FromBase64Url(token[..dot]);
            sig = FromBase64Url(token[(dot + 1)..]);
        }
        catch { reason = "Malformed token."; return false; }

        if (!Common.LinkSigningKeys.Verify(_accepted, payloadBytes, sig))
        { reason = "Invalid token."; return false; }

        // Split from the right. A blob key is `posters/{ownerId}/{id}-{hash}.png`
        // and contains no pipe today, but splitting on the first one would turn
        // a key that ever did into a silently wrong lookup.
        var payload = Encoding.UTF8.GetString(payloadBytes);
        var bar = payload.LastIndexOf('|');
        if (bar <= 0) { reason = "Malformed payload."; return false; }

        if (!long.TryParse(payload[(bar + 1)..], out var expiryUnix))
        { reason = "Bad expiry."; return false; }

        if (DateTimeOffset.UtcNow > DateTimeOffset.FromUnixTimeSeconds(expiryUnix))
        { reason = "This link has expired."; return false; }

        var signedKey = payload[..bar];
        if (signedKey.Length == 0) { reason = "Malformed payload."; return false; }

        blobKey = signedKey;
        return true;
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Decodes base64url. Accepts non-canonical encodings, deliberately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One signature has more than one spelling.</b> The 32-byte HMAC encodes
    /// to 43 characters carrying 258 bits, so the final character has two bits
    /// that decode to nothing. Four different last characters therefore produce
    /// the same bytes, and <see cref="Convert.FromBase64String"/> does not
    /// object — it discards the excess rather than requiring it to be zero.
    /// </para>
    /// <para>
    /// <b>Not a way in.</b> Verification compares decoded bytes against a freshly
    /// computed HMAC, so an attacker still has to produce the right 32 bytes;
    /// being able to respell the last character of a signature they already hold
    /// buys them nothing.
    /// </para>
    /// <para>
    /// <b>What it does mean:</b> the token string is not an identity. Two
    /// different strings can carry the same grant, so a token must never be used
    /// as a cache key, a replay-protection key, or a database unique index
    /// without being canonicalised first. Nothing does that today.
    /// </para>
    /// <para>
    /// This cost a flaky integration test one run in sixteen before anybody
    /// noticed the property existed — see <c>A_tampered_or_missing_token_gets_nothing</c>,
    /// which changed the last character of a token and called it tampering.
    /// </para>
    /// </remarks>
    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }
}
