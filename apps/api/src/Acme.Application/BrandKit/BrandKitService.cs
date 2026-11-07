using Acme.Application.Common;
using Acme.Domain.BrandKit;
using Acme.Domain.Common;
using DomainBrandKit = Acme.Domain.BrandKit.BrandKit;

namespace Acme.Application.BrandKit;

public sealed class BrandKitService(
    IBrandKitRepository repo,
    IBrandAssetService assets)
{
    private static readonly TimeSpan LogoUrlValidity = TimeSpan.FromMinutes(15);

    public async Task<Result<BrandKitDto>> GetForOwnerAsync(string ownerId, CancellationToken ct)
    {
        var kit = await repo.FindByOwnerAsync(ownerId, ct);
        return kit is null
            ? Result<BrandKitDto>.NotFound("No brand kit configured.")
            : Result<BrandKitDto>.Success(ToDto(kit));
    }

    public async Task<Result<BrandKitDto>> UpsertAsync(
        string ownerId,
        UpsertBrandKitRequest request,
        CancellationToken ct)
    {
        // Loaded before validation, because building the new PharmacyDetails
        // needs the booking link already on file to decide what "not supplied"
        // means.
        var existing = await repo.FindByOwnerAsync(ownerId, ct);

        BrandColors colors;
        PharmacyDetails pharmacy;
        try
        {
            colors = BrandColors.Create(
                request.Colors.Primary,
                request.Colors.Secondary,
                request.Colors.Accent);
            pharmacy = PharmacyDetails.Create(
                request.Pharmacy.Name,
                request.Pharmacy.LicenseNumber,
                request.Pharmacy.Phone,
                request.Pharmacy.Address,
                // Absent leaves what is on file; an empty string clears. The
                // caller has to be able to say "I am not touching this", or
                // every client that predates the field silently deletes it.
                request.Pharmacy.BookingUrl ?? existing?.Pharmacy.BookingUrl);
        }
        catch (DomainException ex) { return Result<BrandKitDto>.Invalid(ex.Message); }

        if (existing is null)
        {
            DomainBrandKit kit;
            try
            {
                kit = DomainBrandKit.Create(
                    ownerId,
                    request.Name,
                    colors,
                    pharmacy,
                    request.RegulatoryFooter ?? string.Empty);
            }
            catch (DomainException ex) { return Result<BrandKitDto>.Invalid(ex.Message); }

            await repo.AddAsync(kit, ct);
            return Result<BrandKitDto>.Success(ToDto(kit));
        }

        try
        {
            existing.Update(request.Name, colors, pharmacy, request.RegulatoryFooter ?? string.Empty);
        }
        catch (DomainException ex) { return Result<BrandKitDto>.Invalid(ex.Message); }

        await repo.UpdateAsync(existing, ct);
        return Result<BrandKitDto>.Success(ToDto(existing));
    }

    public async Task<Result<BrandKitDto>> UploadLogoAsync(
        string ownerId,
        Stream content,
        string contentType,
        string fileExtension,
        CancellationToken ct)
    {
        var kit = await repo.FindByOwnerAsync(ownerId, ct);
        if (kit is null)
            return Result<BrandKitDto>.NotFound("Create a brand kit before uploading a logo.");

        // Replace any prior logo so we don't accumulate orphans.
        var oldKey = kit.LogoBlobKey;

        var blobKey = await assets.UploadLogoAsync(ownerId, content, contentType, fileExtension, ct);
        kit.SetLogo(blobKey);
        await repo.UpdateAsync(kit, ct);

        if (!string.IsNullOrEmpty(oldKey))
        {
            try { await assets.DeleteLogoAsync(oldKey, ct); }
            catch { /* swallow — orphan blob will be cleaned by lifecycle policy */ }
        }

        return Result<BrandKitDto>.Success(ToDto(kit));
    }

    /// <summary>
    /// The owner's logo bytes, for the stable route a canvas references.
    /// </summary>
    /// <returns>Null when they have no kit, or no logo, or the blob is gone.</returns>
    public async Task<(Stream Content, string ContentType)?> OpenLogoAsync(
        string ownerId, CancellationToken ct)
    {
        var kit = await repo.FindByOwnerAsync(ownerId, ct);
        if (kit?.LogoBlobKey is not { } key) return null;

        var stream = await assets.OpenLogoAsync(key, ct);
        if (stream is null) return null;

        return (stream, ContentTypeFor(key));
    }

    /// <summary>
    /// The owner's logo blob key, for renderers that read storage directly.
    /// </summary>
    /// <remarks>
    /// The PDF exporter cannot fetch the route: that would be the API calling
    /// itself over a URL taken from a saved document, which inside a container
    /// network often resolves to nothing. It rewrites the reference to this key
    /// and reads the blob.
    /// </remarks>
    public async Task<string?> LogoBlobKeyAsync(string ownerId, CancellationToken ct) =>
        (await repo.FindByOwnerAsync(ownerId, ct))?.LogoBlobKey;

    private static string ContentTypeFor(string blobKey)
    {
        var ext = System.IO.Path.GetExtension(blobKey).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "svg" => "image/svg+xml",
            "webp" => "image/webp",
            // Deliberately not application/octet-stream: that makes a browser
            // download the logo instead of drawing it.
            _ => "image/png",
        };
    }

    private BrandKitDto ToDto(DomainBrandKit kit)
    {
        var url = kit.LogoBlobKey is null
            ? null
            : assets.GetReadOnlyUrl(kit.LogoBlobKey, LogoUrlValidity).ToString();

        return new BrandKitDto(
            kit.Id,
            kit.OwnerId,
            kit.Name,
            kit.LogoBlobKey,
            url,
            new BrandColorsDto(kit.Colors.Primary, kit.Colors.Secondary, kit.Colors.Accent),
            new PharmacyDetailsDto(
                kit.Pharmacy.Name,
                kit.Pharmacy.LicenseNumber,
                kit.Pharmacy.Phone,
                kit.Pharmacy.Address,
                kit.Pharmacy.BookingUrl),
            kit.RegulatoryFooter,
            kit.CreatedAtUtc,
            kit.UpdatedAtUtc);
    }
}
