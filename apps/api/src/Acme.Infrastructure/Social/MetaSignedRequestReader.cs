using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Acme.Application.Social;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Social;

/// <summary>
/// Verifies Meta's <c>signed_request</c>: <c>base64url(signature).base64url(payload)</c>,
/// where the signature is HMAC-SHA256 of the encoded payload under the app secret.
/// </summary>
/// <remarks>
/// The signature is the whole of the authentication on these callbacks — they
/// are anonymous by necessity, since Meta has no session with us. Verified in
/// constant time, before the payload is parsed, and the payload's own
/// <c>algorithm</c> must also say HMAC-SHA256 so a request cannot talk us into
/// something weaker.
/// </remarks>
public sealed class MetaSignedRequestReader(IOptions<MetaOptions> options, TimeProvider? clock = null)
    : IMetaSignedRequestReader
{
    /// <summary>How old a signed request may be and still be honoured.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public string? ReadUserId(string? signedRequest)
    {
        var secret = options.Value.AppSecret;
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(signedRequest)) return null;

        // Generous ceiling: a real one is a few hundred bytes.
        if (signedRequest.Length > 4096) return null;

        var parts = signedRequest.Split('.');
        if (parts.Length != 2) return null;

        byte[] signature, payload;
        try
        {
            signature = FromBase64Url(parts[0]);
            payload = FromBase64Url(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(parts[1]));
        if (!CryptographicOperations.FixedTimeEquals(expected, signature)) return null;

        try
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;

            if (!root.TryGetProperty("algorithm", out var algorithm)
                || !string.Equals(algorithm.GetString(), "HMAC-SHA256", StringComparison.OrdinalIgnoreCase))
                return null;

            // A captured request must not be replayable later — a stale data
            // deletion would delete accounts the person has since reconnected.
            // Meta sends issued_at (Unix seconds) on these callbacks; a day is
            // generous for delivery and retries.
            if (root.TryGetProperty("issued_at", out var issued)
                && issued.ValueKind == JsonValueKind.Number
                && issued.TryGetInt64(out var issuedAt)
                && DateTimeOffset.FromUnixTimeSeconds(issuedAt) < time.GetUtcNow().Subtract(MaxAge))
                return null;

            if (!root.TryGetProperty("user_id", out var userId)) return null;

            // Meta sends it as a string; tolerate a number rather than lose the request.
            return userId.ValueKind switch
            {
                JsonValueKind.String => userId.GetString() is { Length: > 0 } s ? s : null,
                JsonValueKind.Number => userId.GetRawText(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Encodes as Meta does — for tests that need a genuine signed request.</summary>
    public static string Sign(string payloadJson, string appSecret)
    {
        var payload = ToBase64Url(Encoding.UTF8.GetBytes(payloadJson));
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.ASCII.GetBytes(payload));
        return $"{ToBase64Url(signature)}.{payload}";
    }

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
