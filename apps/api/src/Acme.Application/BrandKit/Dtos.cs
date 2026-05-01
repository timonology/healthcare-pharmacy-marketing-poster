namespace Acme.Application.BrandKit;

public sealed record BrandColorsDto(string Primary, string Secondary, string Accent);

public sealed record PharmacyDetailsDto(
    string Name,
    string LicenseNumber,
    string Phone,
    string Address);

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
