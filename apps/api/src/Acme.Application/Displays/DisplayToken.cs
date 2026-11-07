using System.Security.Cryptography;
using System.Text;

namespace Acme.Application.Displays;

/// <summary>
/// The long random secret a screen presents to fetch its content.
/// </summary>
/// <remarks>
/// <para>
/// <b>Random, not signed.</b> <c>VideoDownloadToken</c> next door is an HMAC of
/// a payload, which is right for a link mailed to thousands of patients that
/// must expire on its own and that nobody will ever revoke individually. A
/// screen is the opposite case: a handful of long-lived credentials that a
/// pharmacy must be able to take away the moment a television is sold, moved,
/// or stolen. A signed token cannot be revoked without a list of the revoked
/// ones — at which point the database lookup is happening anyway, and the
/// signature has bought nothing.
/// </para>
/// <para>
/// So the token is 32 bytes from the CSPRNG and the database holds only its
/// SHA-256. Lookup is by hash, which is a single indexed read, and a stolen
/// database dump yields nothing that can be presented to the API.
/// </para>
/// <para>
/// Plain SHA-256 rather than a password hash on purpose. Slow hashing exists to
/// frustrate guessing a human-chosen secret; there is nothing to guess in 256
/// bits of entropy, and a kiosk polling every thirty seconds should not cost a
/// KDF on every request.
/// </para>
/// </remarks>
public static class DisplayToken
{
    /// <summary>Bytes of entropy. 256 bits is not a number anybody searches.</summary>
    private const int TokenBytes = 32;

    /// <summary>Mints a token. The raw value is returned once and never stored.</summary>
    public static (string Raw, string Hash) Issue()
    {
        var raw = ToBase64Url(RandomNumberGenerator.GetBytes(TokenBytes));
        return (raw, HashOf(raw));
    }

    /// <summary>Hashes a presented token so it can be looked up.</summary>
    public static string HashOf(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Whether a presented string could be one of ours.
    /// </summary>
    /// <remarks>
    /// Cheap shape check before touching the database, so a flood of junk
    /// tokens from a scanner costs a length comparison rather than a query.
    /// </remarks>
    public static bool LooksWellFormed(string? token) =>
        !string.IsNullOrWhiteSpace(token)
        && token.Length is >= 40 and <= 64
        && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
