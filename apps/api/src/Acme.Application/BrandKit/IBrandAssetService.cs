namespace Acme.Application.BrandKit;

/// <summary>
/// Brand-asset operations against object storage. Implemented in
/// Infrastructure on top of <c>IBlobStorageService</c>.
/// </summary>
public interface IBrandAssetService
{
    Task<string> UploadLogoAsync(
        string ownerId,
        Stream content,
        string contentType,
        string fileExtension,
        CancellationToken ct);

    Task DeleteLogoAsync(string blobKey, CancellationToken ct);

    /// <summary>SAS URL valid for the given duration. Use a short window for embeds.</summary>
    Uri GetReadOnlyUrl(string blobKey, TimeSpan validFor);
}
