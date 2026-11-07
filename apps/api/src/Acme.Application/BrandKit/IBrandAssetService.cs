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
    /// <remarks>
    /// For showing the logo on a page that is open now. Never write the result
    /// into a canvas: it expires, and a saved document outlives it by years.
    /// </remarks>
    Uri GetReadOnlyUrl(string blobKey, TimeSpan validFor);

    /// <summary>
    /// The logo's bytes, or null when the blob is gone.
    /// </summary>
    /// <remarks>
    /// Added so the logo can be served from a stable, session-authenticated
    /// route instead of a signed URL. That route is what a canvas references,
    /// which is what makes a poster still show its logo next year.
    /// </remarks>
    Task<Stream?> OpenLogoAsync(string blobKey, CancellationToken ct);
}
