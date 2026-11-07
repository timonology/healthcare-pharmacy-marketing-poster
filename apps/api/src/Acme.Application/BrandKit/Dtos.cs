namespace Acme.Application.BrandKit;

public sealed record BrandColorsDto(string Primary, string Secondary, string Accent);

public sealed record PharmacyDetailsDto(
    string Name,

    /// <summary>Optional — see PharmacyDetails.LicenseNumber for why.</summary>
    string LicenseNumber,

    string Phone,
    string Address,

    /// <summary>
    /// Where a patient books. The single stored copy; the profile only shows it.
    /// </summary>
    /// <remarks>
    /// Defaulted so an older client that omits it is not treated as clearing it.
    /// That distinction has already cost this codebase a silent data loss once:
    /// onboarding did not carry the field, sent null, and null meant clear — so
    /// finishing setup wiped a link set on another page, and the only symptom
    /// was a gap on the next poster.
    /// </remarks>
    string? BookingUrl = null);

public sealed record BrandKitDto(
    string Id,
    string OwnerId,
    string Name,
    string? LogoBlobKey,
    string? LogoUrl,
    BrandColorsDto Colors,
    PharmacyDetailsDto Pharmacy,
    string RegulatoryFooter,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record UpsertBrandKitRequest(
    string Name,
    BrandColorsDto Colors,
    PharmacyDetailsDto Pharmacy,
    string RegulatoryFooter);
