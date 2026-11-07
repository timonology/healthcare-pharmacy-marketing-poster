namespace Acme.Application.Common;

/// <summary>
/// Personal data, made fit for a log line (security review R2-15).
/// </summary>
/// <remarks>
/// Logs are kept for weeks, read by more people than the database, and shipped
/// to places the database never goes. An email address in a log line is
/// personal data held without a reason. The masked form keeps what an operator
/// needs to tell two accounts apart in a stream of lines — first and last
/// letter, and the domain — and drops the rest. The audit log, which records
/// who did what, keeps its own full attribution under its own access rules.
/// </remarks>
public static class PersonalData
{
    /// <summary>"jane.doe@example.com" → "j•••e@example.com"; anything else → "•••".</summary>
    public static string Mask(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "•••";
        var value = email.Trim();
        var at = value.IndexOf('@');
        if (at <= 0) return "•••";
        var local = value[..at];
        var shown = local.Length <= 2 ? local[..1] + "•••" : $"{local[0]}•••{local[^1]}";
        return shown + value[at..];
    }

    /// <summary>"+447700900123" → "•••123": enough to tell two numbers apart.</summary>
    public static string MaskPhone(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length <= 3 ? "•••" : "•••" + digits[^3..];
    }
}
