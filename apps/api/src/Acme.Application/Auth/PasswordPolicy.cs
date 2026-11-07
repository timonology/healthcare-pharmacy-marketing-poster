using System.Text;

namespace Acme.Application.Auth;

/// <summary>
/// The one rule for a new password: sign-up, reset and invitation acceptance
/// (security review SEC-13).
/// </summary>
/// <remarks>
/// <para>
/// Length is the measure that matters; composition rules ("one capital, one
/// symbol") push people to <c>Password1!</c>. Twelve characters is the floor,
/// and a short list of the passwords every stuffing list starts with is
/// refused outright. Existing passwords are not affected — this applies when
/// one is chosen.
/// </para>
/// <para>
/// The upper bound is BCrypt's: it reads 72 bytes and ignores the rest, so a
/// longer password would be silently truncated. Saying so is better than
/// letting someone believe the last half of a passphrase counts.
/// </para>
/// </remarks>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxBytes = 72;

    private static readonly string[] Common =
    [
        "password", "qwerty", "qwertyuiop", "letmein", "welcome", "iloveyou", "admin",
        "pharmacy", "monkey", "dragon", "football", "abc", "changeme", "passwordpassword",
    ];

    /// <summary>Why this password cannot be used, or null when it can.</summary>
    public static string? WhyRejected(string? password, string? email = null)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinLength)
            return $"Password must be at least {MinLength} characters.";

        if (Encoding.UTF8.GetByteCount(password) > MaxBytes)
            return $"Password must be {MaxBytes} characters or fewer.";

        var lowered = password.ToLowerInvariant();

        // "Password2024!" and "P@ssw0rd!!!!" are the same guess as "password":
        // compare the letters alone, and the letters after undoing the usual
        // swaps (both, since undoing them turns "2024" into letters too).
        var plain = LettersOf(lowered);
        var unswapped = LettersOf(lowered
            .Replace('0', 'o').Replace('@', 'a').Replace('$', 's').Replace('1', 'i').Replace('3', 'e'));
        if (plain.Length == 0 || Common.Contains(plain) || Common.Contains(unswapped))
            return "That password is too common. Try a short phrase of three or four words.";

        var local = email?.Split('@')[0].ToLowerInvariant();
        if (!string.IsNullOrEmpty(local) && local.Length >= 4 && lowered.Contains(local, StringComparison.Ordinal))
            return "Don't use your email address in your password.";

        if (lowered.Distinct().Count() < 4)
            return "That password is too easy to guess. Try a short phrase of three or four words.";

        return null;
    }

    private static string LettersOf(string value) => new(value.Where(char.IsLetter).ToArray());
}
