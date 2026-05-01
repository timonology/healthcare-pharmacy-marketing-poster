using Acme.Application.BrandKit;

namespace Acme.Infrastructure.Storage;

public sealed class AzureBrandAssetService(IBlobStorageService blobs) : IBrandAssetService
{
    private const string LogoPrefix = "brand/logos";

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/svg+xml",
        "image/webp",
    };

    public async Task<string> UploadLogoAsync(
        string ownerId,
        Stream content,
        string contentType,
        string fileExtension,
        CancellationToken ct)
    {
        if (!AllowedContentTypes.Contains(contentType))
            throw new InvalidOperationException(
                $"Unsupported logo content type: {contentType}");

        var sanitizedExt = (fileExtension ?? string.Empty).TrimStart('.');
        if (string.IsNullOrEmpty(sanitizedExt)) sanitizedExt = "bin";

        var blobKey = $"{LogoPrefix}/{ownerId}/{Guid.NewGuid():N}.{sanitizedExt}";
        await blobs.UploadAsync(blobKey, content, contentType, ct);
        return blobKey;
    }

    public Task DeleteLogoAsync(string blobKey, CancellationToken ct) =>
        blobs.DeleteAsync(blobKey, ct);

    public Uri GetReadOnlyUrl(string blobKey, TimeSpan validFor) =>
        blobs.GetReadOnlyUri(blobKey, validFor);
}
