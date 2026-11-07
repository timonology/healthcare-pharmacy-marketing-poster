using Acme.Application.Templates;

namespace Acme.Infrastructure.Storage;

/// <inheritdoc />
public sealed class TemplateAssetService(IBlobStorageService blobs) : ITemplateAssetService
{
    /// <summary>
    /// SVG is deliberately absent. These files are served from our own origin,
    /// and an SVG can carry script — accepting one would be a stored-XSS vector
    /// against everyone who opens the gallery. Brand logos get away with SVG
    /// because they are served from a separate blob-storage origin.
    /// </summary>
    private static bool IsAllowed(string contentType) =>
        TemplateAssetUrls.ExtensionByContentType.ContainsKey(contentType);

    public async Task<StoredTemplateAsset> UploadAsync(
        Stream content,
        string contentType,
        string fileExtension,
        CancellationToken ct)
    {
        if (!IsAllowed(contentType))
        {
            throw new InvalidOperationException(
                $"Unsupported image type '{contentType}'. Use PNG, JPEG or WebP.");
        }

        // The stored name is generated, never derived from the upload. A
        // caller-supplied filename is the classic path-traversal and
        // content-sniffing hole, and nothing downstream needs the original.
        var ext = TemplateAssetUrls.ExtensionByContentType[contentType];
        var fileName = $"{Guid.NewGuid():N}.{ext}";
        var blobKey = $"{TemplateAssetUrls.BlobPrefix}/{fileName}";

        await blobs.UploadAsync(blobKey, content, contentType, ct);

        return new StoredTemplateAsset(
            blobKey, TemplateAssetUrls.UrlFor(fileName), contentType);
    }

    public async Task<Stream?> OpenAsync(string fileName, CancellationToken ct)
    {
        if (!TemplateAssetUrls.IsSafeFileName(fileName)) return null;
        try
        {
            return await blobs.DownloadAsync(
                $"{TemplateAssetUrls.BlobPrefix}/{fileName}", ct);
        }
        catch (Exception)
        {
            // Missing blob, or storage unavailable. Either way the caller
            // renders a 404 rather than a 500 — a template referencing a
            // deleted asset should degrade, not break the page.
            return null;
        }
    }

    public bool IsAssetUrl(string urlOrPath) =>
        TemplateAssetUrls.TryResolveBlobKey(urlOrPath) is not null;

    public string? TryResolveBlobKey(string urlOrPath) =>
        TemplateAssetUrls.TryResolveBlobKey(urlOrPath);
}
