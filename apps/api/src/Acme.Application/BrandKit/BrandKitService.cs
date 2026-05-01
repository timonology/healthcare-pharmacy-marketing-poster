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
                request.Pharmacy.Address);
        }
        catch (DomainException ex) { return Result<BrandKitDto>.Invalid(ex.Message); }

        var existing = await repo.FindByOwnerAsync(ownerId, ct);
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
                kit.Pharmacy.Address),
            kit.RegulatoryFooter,
            kit.CreatedAtUtc,
            kit.UpdatedAtUtc);
    }
}
