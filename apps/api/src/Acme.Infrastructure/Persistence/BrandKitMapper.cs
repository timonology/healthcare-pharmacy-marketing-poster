using System.Reflection;
using Acme.Domain.BrandKit;
using DomainBrandKit = Acme.Domain.BrandKit.BrandKit;

namespace Acme.Infrastructure.Persistence;

internal static class BrandKitMapper
{
    private static readonly ConstructorInfo Ctor = typeof(DomainBrandKit)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("BrandKit parameterless ctor missing.");

    public static DomainBrandKit Hydrate(BrandKitDocument d)
    {
        var kit = (DomainBrandKit)Ctor.Invoke(null);
        Set(kit, nameof(DomainBrandKit.Id), d.Id);
        Set(kit, nameof(DomainBrandKit.OwnerId), d.OwnerId);
        Set(kit, nameof(DomainBrandKit.Name), d.Name);
        Set(kit, nameof(DomainBrandKit.LogoBlobKey), d.LogoBlobKey);
        Set(kit, nameof(DomainBrandKit.Colors), BrandColors.Create(
            d.Colors.Primary, d.Colors.Secondary, d.Colors.Accent));
        // TryNormalize, not Normalize. Reading a row must not throw on what is
        // already in it: a link that predates the validator, or that a migration
        // carried over from a profile, would otherwise take down every request
        // that touches this pharmacy's brand kit — and the value is re-validated
        // properly the next time somebody saves it. Create re-normalises, which
        // is idempotent for anything TryNormalize returns.
        Set(kit, nameof(DomainBrandKit.Pharmacy), PharmacyDetails.Create(
            d.Pharmacy.Name, d.Pharmacy.LicenseNumber, d.Pharmacy.Phone, d.Pharmacy.Address,
            BookingLink.TryNormalize(d.Pharmacy.BookingUrl)));
        Set(kit, nameof(DomainBrandKit.RegulatoryFooter), d.RegulatoryFooter);
        Set(kit, nameof(DomainBrandKit.CreatedAtUtc), d.CreatedAtUtc);
        Set(kit, nameof(DomainBrandKit.UpdatedAtUtc), d.UpdatedAtUtc);
        return kit;
    }

    public static BrandKitDocument ToDocument(DomainBrandKit kit) => new()
    {
        Id = kit.Id,
        OwnerId = kit.OwnerId,
        Name = kit.Name,
        LogoBlobKey = kit.LogoBlobKey,
        Colors = new BrandColorsDocument
        {
            Primary = kit.Colors.Primary,
            Secondary = kit.Colors.Secondary,
            Accent = kit.Colors.Accent,
        },
        Pharmacy = new PharmacyDetailsDocument
        {
            Name = kit.Pharmacy.Name,
            LicenseNumber = kit.Pharmacy.LicenseNumber,
            Phone = kit.Pharmacy.Phone,
            Address = kit.Pharmacy.Address,
            BookingUrl = kit.Pharmacy.BookingUrl,
        },
        RegulatoryFooter = kit.RegulatoryFooter,
        CreatedAtUtc = kit.CreatedAtUtc,
        UpdatedAtUtc = kit.UpdatedAtUtc,
    };

    private static void Set(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}
