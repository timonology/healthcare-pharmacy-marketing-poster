namespace Acme.Infrastructure.Persistence;

/// <summary>
/// Whether a smoke-test credential may be created in this environment.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and separate from <see cref="SmokeAccountSeeder"/>, so the rule can be
/// tested without a host, a database or a repository fake. The seeder's job is
/// then only to ask and obey.
/// </para>
/// <para>
/// The rule exists because a smoke account is a known address with a written-down
/// password. On a laptop that is the whole point. On a real deployment it is an
/// unattended door into a live pharmacy tenant, so production has to demand a
/// credential that is neither guessable nor published.
/// </para>
/// </remarks>
public static class SmokeAccountPolicy
{
    /// <summary>
    /// The password shipped in <c>.env.example</c>, <c>appsettings.json</c> and
    /// <c>docker-compose.yml</c>.
    /// </summary>
    /// <remarks>
    /// Refused by value outside Development. A length rule alone would accept a
    /// nineteen-character password that is printed in a public repository —
    /// which is precisely the credential an attacker would try first.
    /// </remarks>
    public const string PublishedDevPassword = "Smoke-Dev-Only-123!";

    /// <summary>Minimum length outside Development.</summary>
    public const int MinimumLength = 16;

    private static readonly string[] ObviouslyWeak =
    {
        "changeme", "password", "admin", "smoke123", "letmein", "123456",
    };

    /// <summary>
    /// Null when this password may be used here, or the reason it may not.
    /// </summary>
    /// <param name="password">The configured password.</param>
    /// <param name="isDevelopment">
    /// True on a developer's machine, where a shared known password is the
    /// intended behaviour and every other check is off.
    /// </param>
    public static string? WhyPasswordRefused(string? password, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(password))
            return "SmokeAccount:Password is empty.";

        if (isDevelopment) return null;

        if (string.Equals(password, PublishedDevPassword, StringComparison.Ordinal))
            return "SmokeAccount:Password is the password published in this repository. "
                + "Set it to something else outside Development.";

        if (password.Length < MinimumLength)
            return $"SmokeAccount:Password is shorter than {MinimumLength} characters.";

        var lowered = password.ToLowerInvariant();
        if (ObviouslyWeak.Any(w => lowered.Contains(w, StringComparison.Ordinal)))
            return "SmokeAccount:Password contains an obvious word.";

        return null;
    }
}
