using System.Security.Cryptography;
using System.Text;

namespace Acme.Application.Common;

/// <summary>
/// Keys for the signed public links — emailed poster images, video downloads
/// (security review R2-13).
/// </summary>
/// <remarks>
/// <para>
/// These links used to be signed with the JWT secret itself. That made two
/// things worse than they needed to be: rotating the JWT secret after an
/// incident broke every poster image already sitting in patients' inboxes (180
/// days of them), which discourages the rotation; and one key meant one leak
/// compromised sessions and links together.
/// </para>
/// <para>
/// Now each kind of link has its own key, derived with HKDF from
/// <c>Signing:LinkSecret</c> (falling back to the JWT secret until that is
/// set). Links signed the old way keep verifying until they expire, so nothing
/// already sent breaks.
/// </para>
/// </remarks>
public static class LinkSigningKeys
{
    /// <summary>A 256-bit key for one purpose, derived from a master secret.</summary>
    public static byte[] Derive(string masterSecret, string purpose) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(masterSecret),
            outputLength: 32,
            salt: Array.Empty<byte>(),
            info: Encoding.UTF8.GetBytes($"marketing25/link/{purpose}/v1"));

    /// <summary>
    /// The key to sign with, then every key a link may still have been signed
    /// with: this purpose's key under the legacy secret (when the link secret
    /// differs) and the legacy secret's raw bytes (links from before R2-13).
    /// </summary>
    /// <remarks>
    /// The raw legacy key can be dropped once the longest link signed with it
    /// has expired: 180 days after this change shipped.
    /// </remarks>
    public static (byte[] Signing, byte[][] Accepted) For(string purpose, string linkSecret, string? legacySecret)
    {
        var signing = Derive(linkSecret, purpose);
        var accepted = new List<byte[]> { signing };
        if (!string.IsNullOrEmpty(legacySecret))
        {
            if (legacySecret != linkSecret) accepted.Add(Derive(legacySecret, purpose));
            accepted.Add(Encoding.UTF8.GetBytes(legacySecret));
        }
        return (signing, accepted.ToArray());
    }

    /// <summary>Whether <paramref name="signature"/> is HMAC-SHA256 of the payload under any accepted key.</summary>
    public static bool Verify(byte[][] accepted, byte[] payload, byte[] signature)
    {
        var ok = false;
        foreach (var key in accepted)
        {
            // Every key is tried, so timing does not reveal which one matched.
            ok |= CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(key, payload));
        }
        return ok;
    }
}
