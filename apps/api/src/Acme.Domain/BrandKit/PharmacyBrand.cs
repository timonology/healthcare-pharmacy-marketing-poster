using Acme.Domain.Users;

// The namespace and the type share a name, so `BrandKit` on its own is at best
// fragile and at worst CS0118. The rest of the codebase already aliases it —
// see IBrandKitRepository and BrandKitService — and this follows suit rather
// than relying on innermost-first lookup happening to pick the type.
using DomainBrandKit = Acme.Domain.BrandKit.BrandKit;

namespace Acme.Domain.BrandKit;

/// <summary>
/// One pharmacy's identity, resolved from the two records that hold it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> A pharmacy's name lives in two places — the
/// <see cref="PharmacyProfile"/> that onboarding collects, and the
/// <c>Pharmacy</c> block on the <see cref="DomainBrandKit"/> that the designer
/// collects. Three surfaces resolved that disagreement three different ways
/// before this existed:
/// </para>
/// <list type="bullet">
///   <item>Poster tokens took the profile and never looked at the brand kit.</item>
///   <item>The campaign sender name took the profile, then the brand kit.</item>
///   <item>Video was specified to take the brand kit, then the profile — the
///         opposite order, which would have put a different pharmacy name on a
///         video from the one on the poster beside it in the same campaign.</item>
/// </list>
/// <para>
/// That is the fault shape behind most of what has gone wrong in this codebase:
/// one rule, written out by hand in several places, drifting. So the rule is
/// here, once, and every surface asks this type rather than deciding for itself.
/// </para>
/// <para>
/// <b>The order is profile first.</b> The profile is what onboarding asks for,
/// what the app nags about until it is finished, and what posters already print.
/// The brand kit fills gaps rather than winning, because a pharmacy correcting
/// its name is overwhelmingly likely to do it where it was asked for it.
/// </para>
/// <para>
/// The exceptions are the fields the profile has no equivalent for — the logo,
/// the colours, the licence number and the regulatory footer. Those come from
/// the brand kit unconditionally, because there is nothing to prefer over them.
/// </para>
/// </remarks>
public sealed record PharmacyBrand
{
    /// <summary>What to call the pharmacy. Never blank.</summary>
    /// <remarks>
    /// Falls back to "Pharmacy" rather than the account holder's name. A patient
    /// reading "Pharmacy" learns little; reading a person's name actively
    /// misleads them about who is writing.
    /// </remarks>
    public required string Name { get; init; }

    /// <summary>Street address and postcode as one line, or "" if unknown.</summary>
    public required string Address { get; init; }

    /// <summary>Public phone number, or "".</summary>
    public required string Phone { get; init; }

    /// <summary>Where a patient books, already normalised, or null.</summary>
    /// <remarks>
    /// Brand kit only. It was profile-only until the link moved — see
    /// <see cref="PharmacyDetails.BookingUrl"/> — and it is still resolved in one
    /// place, which is the point of this type. <see cref="BookingLink.Normalize"/>
    /// is the single gate that keeps a <c>javascript:</c> URL off a poster and
    /// out of a QR code.
    /// </remarks>
    public required string? BookingUrl { get; init; }

    /// <summary>Brand kit only — the profile has no logo.</summary>
    public required string? LogoBlobKey { get; init; }

    /// <summary>Brand kit only. Null when the pharmacy has no kit yet.</summary>
    public required BrandColors? Colors { get; init; }

    /// <summary>Brand kit only, or "".</summary>
    public required string LicenseNumber { get; init; }

    /// <summary>Brand kit only, or "".</summary>
    public required string RegulatoryFooter { get; init; }

    /// <summary>
    /// True when a real pharmacy name was found in either record.
    /// </summary>
    /// <remarks>
    /// Recorded during resolution rather than inferred by comparing
    /// <see cref="Name"/> to <see cref="FallbackName"/>, because a pharmacy
    /// genuinely trading as "Pharmacy" would make that comparison lie — and the
    /// thing it decides is whether a patient sees branding at all.
    /// </remarks>
    public required bool IsUsable { get; init; }

    /// <summary>What a pharmacy with nothing filled in is called.</summary>
    public const string FallbackName = "Pharmacy";

    /// <summary>
    /// Resolves one pharmacy's identity. Either argument may be null.
    /// </summary>
    /// <param name="profile">The onboarding profile. Wins wherever it is filled in.</param>
    /// <param name="kit">The brand kit. Fills gaps, and owns logo, colours, licence and footer.</param>
    /// <remarks>
    /// <para>
    /// <b>Not a replacement for <see cref="User.PharmacyDisplayName"/>.</b> That
    /// one falls back to the account holder's own name, which is right for a
    /// header the pharmacist is looking at and wrong for anything a patient
    /// reads. Two rules, deliberately — the difference is the audience, and
    /// collapsing them would put a person's name on a poster.
    /// </para>
    /// </remarks>
    public static PharmacyBrand Resolve(PharmacyProfile? profile, DomainBrandKit? kit)
    {
        // A single definition of "this field has something in it", applied to
        // both records. The previous copies of this logic differed on whether
        // whitespace counted, which is how a profile containing " " could beat
        // a brand kit that was properly filled in.
        static string? Filled(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        static string First(string? preferred, string? fallback, string ifNeither) =>
            Filled(preferred) ?? Filled(fallback) ?? ifNeither;

        // Address is assembled rather than picked, because the profile splits it
        // across two fields and the brand kit holds it as one. Taking the
        // profile's street without its postcode would be worse than taking the
        // kit's complete line.
        var profileAddress = string.Join(", ", new[] { profile?.Address, profile?.PostCode }
            .Select(Filled)
            .Where(p => p is not null));

        var foundName = Filled(profile?.PharmacyName) ?? Filled(kit?.Pharmacy.Name);

        return new PharmacyBrand
        {
            Name = foundName ?? FallbackName,
            IsUsable = foundName is not null,
            Address = First(profileAddress, kit?.Pharmacy.Address, string.Empty),
            Phone = First(profile?.ContactPhone, kit?.Pharmacy.Phone, string.Empty),

            // Not First(...): there is no second place to look.
            BookingUrl = Filled(kit?.Pharmacy.BookingUrl),
            LogoBlobKey = Filled(kit?.LogoBlobKey),
            Colors = kit?.Colors,
            LicenseNumber = Filled(kit?.Pharmacy.LicenseNumber) ?? string.Empty,
            RegulatoryFooter = Filled(kit?.RegulatoryFooter) ?? string.Empty,
        };
    }

    /// <summary>Nothing known about this pharmacy.</summary>
    public static PharmacyBrand Unknown() => Resolve(null, null);
}
