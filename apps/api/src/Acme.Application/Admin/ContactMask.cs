namespace Acme.Application.Admin;

/// <summary>
/// Turns a patient's contact details into something an administrator can work
/// with but not use.
/// </summary>
/// <remarks>
/// The support view exists so an administrator can answer "the email never
/// arrived" without also handing them a downloadable copy of the pharmacy's
/// patient list. Masking is what makes that distinction real, so a few
/// properties are deliberate rather than incidental:
///
///   - <b>The mask is fixed width.</b> Five bullets whatever the input, so the
///     rendering does not leak the length of what it hides. A proportional
///     mask would narrow a guess considerably on a short local part.
///
///   - <b>The email domain survives.</b> This is the one concession, and it is
///     the reason support can diagnose anything: "all fourteen failures are on
///     the same corporate domain" is the answer to most delivery queries, and
///     it cannot be reached from a fully opaque address. A domain is shared by
///     millions of people; the local part is the identifying half.
///
///   - <b>Malformed input masks harder, never softer.</b> Anything that does
///     not parse is replaced wholesale. The failure mode of a masking function
///     must be to reveal less, and the easiest way to get that wrong is to
///     fall through to <c>return input</c> on an unexpected shape.
///
/// Nothing here is reversible, and nothing here should be treated as
/// anonymisation — a masked address is still personal data, and the reveal
/// path exists precisely so that access to the real value is recorded.
/// </remarks>
public static class ContactMask
{
    private const string Bullets = "•••••";

    /// <summary>
    /// Masks an email address, keeping the first character of the local part
    /// and the whole domain: <c>jane.doe@gmail.com</c> becomes
    /// <c>j•••••@gmail.com</c>.
    /// </summary>
    /// <returns>Null when there was nothing to mask.</returns>
    public static string? Email(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        var trimmed = email.Trim();

        // LastIndexOf, not IndexOf: a local part may legally contain a quoted
        // '@', and splitting on the first one would put part of the local part
        // into the "domain" and publish it.
        var at = trimmed.LastIndexOf('@');

        // No domain to preserve, or nothing before the '@'. Either way there is
        // no safe partial rendering, so nothing is kept.
        if (at <= 0 || at == trimmed.Length - 1) return Bullets;

        var local = trimmed[..at];
        var domain = trimmed[(at + 1)..];

        // A one- or two-character local part is mostly its first character.
        // Keeping it would disclose the majority of what we are hiding.
        var head = local.Length >= 3 ? local[..1] : string.Empty;

        return $"{head}{Bullets}@{domain}";
    }

    /// <summary>
    /// Masks a phone number down to its last three digits:
    /// <c>+447700900123</c> becomes <c>•••••123</c>.
    /// </summary>
    /// <remarks>
    /// Three, not the four that card displays use. Four digits of a UK mobile
    /// leaves a narrow enough space that a determined guess against a known
    /// area is plausible, and support does not need the extra digit — three is
    /// already enough for a patient to confirm "yes, that's my number" over
    /// the phone, which is the only thing it is for.
    /// </remarks>
    /// <returns>Null when there was nothing to mask.</returns>
    public static string? Phone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;

        var digits = new string(phone.Where(char.IsDigit).ToArray());

        // Too short to show any of it without showing most of it.
        if (digits.Length < 6) return Bullets;

        return Bullets + digits[^3..];
    }

    /// <summary>
    /// Masks a campaign recipient, which may be either an email address or a
    /// phone number depending on the campaign's channel.
    /// </summary>
    /// <remarks>
    /// Always returns a string rather than null: a delivery report row exists
    /// because something was sent somewhere, so "no recipient" is not a state
    /// worth representing — a blank cell would read as a UI bug.
    /// </remarks>
    public static string Recipient(string? recipient)
    {
        if (string.IsNullOrWhiteSpace(recipient)) return Bullets;

        return recipient.Contains('@')
            ? Email(recipient) ?? Bullets
            : Phone(recipient) ?? Bullets;
    }
}
