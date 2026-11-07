using System.Security.Cryptography;
using System.Text;

namespace Acme.Application.Videos;

/// <summary>
/// HMAC-signed short-lived token for public /api/videos/{id}/public-download
/// links used in outgoing campaign emails. Keeps the recipient list from
/// needing an auth cookie while still preventing arbitrary access — the token
/// binds the video id + a `t` (thumbnail) flag + expiry, and is signed with
/// its own key derived from Signing:LinkSecret (security review R2-13; see
/// LinkSigningKeys). Links signed under the JWT secret before that still verify.
///
/// Token format:  base64url(payload) + "." + base64url(hmac)
/// Payload:       {videoId}|{t=0|1}|{unix_expiry}
/// </summary>
public static class VideoDownloadToken
{
    /// <summary>Signing key. Sourced from JWT__SECRET so operators only
    /// have one secret to rotate. Set once at app startup via
    /// <see cref="Configure"/>.</summary>
    private static byte[]? _key;

    public static void Configure(string secret) => Configure(secret, legacySecret: secret);

    /// <summary>
    /// Signs with this link type's own key derived from <paramref name="linkSecret"/>,
    /// and still accepts links signed under <paramref name="legacySecret"/> (security review R2-13).
    /// </summary>
    public static void Configure(string linkSecret, string? legacySecret)
    {
        var (signing, accepted) = Common.LinkSigningKeys.For("video-download", linkSecret, legacySecret);
        _key = signing;
        _accepted = accepted;
    }

    private static byte[][] _accepted = Array.Empty<byte[]>();

    public static string Sign(string videoId, bool thumbnail, DateTimeOffset expiresAt)
    {
        var key = _key ?? throw new InvalidOperationException(
            "VideoDownloadToken.Configure must be called at startup.");
        var payload = $"{videoId}|{(thumbnail ? "1" : "0")}|{expiresAt.ToUnixTimeSeconds()}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var mac = new HMACSHA256(key);
        var sig = mac.ComputeHash(payloadBytes);
        return $"{ToBase64Url(payloadBytes)}.{ToBase64Url(sig)}";
    }

    public static bool TryValidate(string token, string expectedVideoId, bool expectedThumbnail, out string? reason)
    {
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

        // Constant-time compare.
        if (!Common.LinkSigningKeys.Verify(_accepted, payloadBytes, sig))
        { reason = "Invalid token."; return false; }

        var payload = Encoding.UTF8.GetString(payloadBytes);
        var parts = payload.Split('|');
        if (parts.Length != 3) { reason = "Malformed payload."; return false; }
        var vid = parts[0];
        var isThumb = parts[1] == "1";
        if (!long.TryParse(parts[2], out var expiryUnix))
        { reason = "Bad expiry."; return false; }
        var expiry = DateTimeOffset.FromUnixTimeSeconds(expiryUnix);

        if (!string.Equals(vid, expectedVideoId, StringComparison.OrdinalIgnoreCase))
        { reason = "Token is for a different video."; return false; }
        if (isThumb != expectedThumbnail)
        { reason = "Token is for a different asset."; return false; }
        if (DateTimeOffset.UtcNow > expiry)
        { reason = "Link expired."; return false; }
        return true;
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }
}
