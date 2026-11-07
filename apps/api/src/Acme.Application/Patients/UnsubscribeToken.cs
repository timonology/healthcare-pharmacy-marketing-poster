using System.Text;
using Acme.Application.Common;

namespace Acme.Application.Patients;

/// <summary>
/// The signed token in a campaign email's unsubscribe link (security review R2-06).
/// </summary>
/// <remarks>
/// Names the pharmacy and the address, and nothing else, signed with the
/// unsubscribe key derived from the link secret. No expiry: an unsubscribe link
/// has to work whenever the person finds the email, and all it can do is stop
/// mail to its own address.
/// </remarks>
public static class UnsubscribeToken
{
    private static byte[]? _key;
    private static byte[][] _accepted = Array.Empty<byte[]>();

    public static void Configure(string linkSecret)
    {
        // No legacy key: these tokens are new with R2-06.
        var (signing, accepted) = LinkSigningKeys.For("unsubscribe", linkSecret, legacySecret: null);
        _key = signing;
        _accepted = accepted;
    }

    public static string Sign(string ownerId, string email)
    {
        var key = _key ?? throw new InvalidOperationException(
            "UnsubscribeToken.Configure must be called at startup.");
        var payload = Encoding.UTF8.GetBytes($"{ownerId}|{Normalise(email)}");
        var sig = System.Security.Cryptography.HMACSHA256.HashData(key, payload);
        return $"{ToBase64Url(payload)}.{ToBase64Url(sig)}";
    }

    public static bool TryRead(string? token, out string ownerId, out string email)
    {
        ownerId = "";
        email = "";
        if (_key is null || string.IsNullOrWhiteSpace(token) || token.Length > 1024) return false;

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) return false;

        byte[] payload, sig;
        try
        {
            payload = FromBase64Url(token[..dot]);
            sig = FromBase64Url(token[(dot + 1)..]);
        }
        catch (FormatException) { return false; }

        if (!LinkSigningKeys.Verify(_accepted, payload, sig)) return false;

        var text = Encoding.UTF8.GetString(payload);
        var bar = text.IndexOf('|');
        if (bar <= 0 || bar == text.Length - 1) return false;

        ownerId = text[..bar];
        email = text[(bar + 1)..];
        return true;
    }

    public static string Normalise(string email) => email.Trim().ToLowerInvariant();

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }
}
