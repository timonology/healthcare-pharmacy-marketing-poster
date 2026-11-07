using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Acme.WebApi;

/// <summary>
/// The caller's real address, when the web app vouches for it (security review R2-03).
/// </summary>
/// <remarks>
/// <para>
/// Every browser request reaches the API through the web app, so the address
/// the API sees is the web app's. The web app passes the browser's address on —
/// but the API is also reachable directly, and an unsigned "the browser was
/// 1.2.3.4" header can be sent by anyone. With the per-address rate limits on
/// sign-in, reset and screen pairing keyed on it, that made the limits
/// per-whatever-the-caller-claimed.
/// </para>
/// <para>
/// So the web app signs the address (HMAC-SHA256 over <c>address|minute</c>,
/// see apps/web/src/lib/server/forwarding.ts) with a key only the two services
/// hold, <c>Edge:ClientIpKey</c>. A valid signature replaces the connection's
/// address; anything else is ignored and the address stays the one the
/// platform's own front end recorded.
/// </para>
/// </remarks>
public sealed class SignedClientIpMiddleware(RequestDelegate next, byte[] key)
{
    public const string AddressHeader = "X-Client-Ip";
    public const string SignatureHeader = "X-Client-Ip-Signature";

    /// <summary>HttpContext.Items key recording what became of the signature, for diagnostics.</summary>
    public const string SignatureItem = "client-ip-signature";
    public const string SignatureValid = "valid";
    public const string SignatureInvalid = "invalid";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Request.Headers;
        var address = headers[AddressHeader].ToString();
        var signature = headers[SignatureHeader].ToString();

        if (address.Length > 0
            && signature.Length > 0
            && IPAddress.TryParse(address, out var ip)
            && IsValid(key, address, signature, DateTimeOffset.UtcNow))
        {
            context.Connection.RemoteIpAddress = ip;
            context.Items[SignatureItem] = SignatureValid;
        }
        else if (signature.Length > 0)
        {
            // Signed, but not with our key: the two services disagree about
            // Edge:ClientIpKey / CLIENT_IP_SIGNING_KEY. Every browser then shares
            // the web app's address for rate limits, which is how one person's
            // retries end up blocking everybody's password reset.
            context.Items[SignatureItem] = SignatureInvalid;
        }

        // Never passed further in: nothing downstream should read them raw.
        headers.Remove(AddressHeader);
        headers.Remove(SignatureHeader);
        return next(context);
    }

    /// <summary>Accepts this minute's signature or the previous minute's.</summary>
    public static bool IsValid(byte[] key, string address, string signature, DateTimeOffset now)
    {
        var minute = now.ToUnixTimeSeconds() / 60;
        return Matches(key, address, signature, minute) || Matches(key, address, signature, minute - 1);
    }

    public static string Sign(byte[] key, string address, long minute)
    {
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{address}|{minute}"));
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool Matches(byte[] key, string address, string signature, long minute) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Sign(key, address, minute)),
            Encoding.ASCII.GetBytes(signature));
}
