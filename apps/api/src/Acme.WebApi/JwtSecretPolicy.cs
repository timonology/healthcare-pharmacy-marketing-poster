using System.Text;

namespace Acme.WebApi;

/// <summary>
/// Refuses to start with a signing secret anyone could know (security review SEC-04).
/// </summary>
/// <remarks>
/// <para>
/// The base configuration ships a placeholder secret, long enough to be
/// accepted. A deployment that lost its <c>Jwt__Secret</c> setting used to
/// start normally and sign every session with that published value — so anyone
/// who had read the repository could mint a SuperAdmin token, and forge the
/// signed poster and video links that use the same key. Nothing failed; nothing
/// was logged.
/// </para>
/// <para>
/// Outside Development a missing, placeholder or short secret is now a startup
/// failure, with the reason. Development only warns, so a fresh clone still runs.
/// </para>
/// </remarks>
public static class JwtSecretPolicy
{
    /// <summary>HS256 wants at least 256 bits of key.</summary>
    public const int MinBytes = 32;

    /// <summary>Words that only appear in placeholder values.</summary>
    private static readonly string[] PlaceholderMarkers =
        ["replace", "change_me", "changeme", "change-me", "your-secret", "example", "placeholder"];

    /// <summary>Why this secret must not sign anything, or null when it is fine.</summary>
    public static string? WhyUnsafe(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            return "Jwt:Secret is not set. Set Jwt__Secret to a long random value.";

        if (PlaceholderMarkers.Any(m => secret.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return "Jwt:Secret is still a placeholder from the repository. Set Jwt__Secret to a "
                   + "long random value (e.g. `openssl rand -base64 48`).";

        if (Encoding.UTF8.GetByteCount(secret) < MinBytes)
            return $"Jwt:Secret is shorter than {MinBytes} bytes. Use a long random value.";

        return null;
    }
}
