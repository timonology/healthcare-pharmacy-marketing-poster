using Acme.Domain.Common;

namespace Acme.Domain.BrandKit;

/// <summary>
/// The one gate every booking link passes through before it is stored.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is its own type.</b> It used to be a static method on
/// <see cref="Users.PharmacyProfile"/>, because that is where the link was kept.
/// The link now belongs to the brand kit, and leaving the validator behind on
/// the profile would have the brand kit reaching into the users namespace for a
/// rule about its own field — or, far more likely, growing a second copy.
/// </para>
/// <para>
/// <b>Why it validates at all.</b> This value is printed on posters, encoded
/// into a QR code somebody scans in a shop window, and put in front of patients.
/// A typo that reaches a printed run cannot be corrected, and a QR is unreadable
/// by the person holding it — they cannot tell a wrong address from a right one
/// until it fails to load.
/// </para>
/// </remarks>
public static class BookingLink
{
    public const int MaxLength = 400;

    /// <summary>
    /// Trims, adds a missing scheme, and refuses anything that is not a web address.
    /// </summary>
    /// <remarks>
    /// A bare <c>booking.mypharmacy.co.uk</c> gets <c>https://</c> rather than a
    /// refusal: that is what a pharmacist types, it is unambiguous, and rejecting
    /// it teaches them the field is fussy instead of helpful. What is refused is
    /// anything that is not http or https — a <c>javascript:</c> or <c>data:</c>
    /// URL here would become a link on a page and a QR code in a waiting room.
    /// </remarks>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxLength)
            throw new DomainException($"Booking link must be {MaxLength} characters or fewer.");

        // No scheme at all, and no other scheme's colon either — assume https
        // rather than rejecting what a person would reasonably type.
        if (!trimmed.Contains("://", StringComparison.Ordinal)
            && !trimmed.Contains(':', StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new DomainException(
                "Booking link must be a web address, like https://booking.yourpharmacy.co.uk");
        }

        if (string.IsNullOrWhiteSpace(uri.Host) || !uri.Host.Contains('.'))
            throw new DomainException(
                "Booking link needs a full domain, like https://booking.yourpharmacy.co.uk");

        // AbsoluteUri, not ToString(). ToString() returns a display form with
        // percent-escapes decoded, which is the wrong thing to store for a value
        // that becomes an href and the payload of a QR code — a decoded space or
        // ampersand in a query string changes where the link goes.
        return uri.AbsoluteUri;
    }

    /// <summary>
    /// Normalises without throwing. For migrating values already in the database.
    /// </summary>
    /// <remarks>
    /// A migration cannot afford an exception on one bad row: it would abort the
    /// pass and leave every pharmacy after that one unmigrated, and the bad value
    /// got in before this gate existed — refusing it now punishes the wrong
    /// people. A link that cannot be normalised is dropped and logged, which is
    /// the same outcome the renderers already produce for it.
    /// </remarks>
    public static string? TryNormalize(string? raw)
    {
        try { return Normalize(raw); }
        catch (DomainException) { return null; }
    }
}
