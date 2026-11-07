using Acme.Application.Posters;

namespace Acme.Infrastructure.Storage;

/// <summary>
/// Rendered poster images in blob storage, under <c>posters/</c>.
/// </summary>
/// <remarks>
/// <para>
/// A thin adapter over <see cref="IBlobStorageService"/> so the application
/// layer never sees the storage SDK. The interesting behaviour is all in
/// <see cref="PosterImageService"/>; this exists to keep the dependency
/// pointing the right way.
/// </para>
/// <para>
/// <see cref="ExistsAsync"/> is deliberately a download attempt rather than a
/// dedicated existence API. <see cref="IBlobStorageService"/> has no Exists,
/// and adding one would ripple through every implementation for a call made
/// once per publish — the extra round trip is cheaper than the churn.
/// </para>
/// </remarks>
public sealed class AzurePosterImageStore(IBlobStorageService blobs) : IPosterImageStore
{
    public async Task UploadAsync(string blobKey, byte[] png, CancellationToken ct)
    {
        using var stream = new MemoryStream(png, writable: false);
        await blobs.UploadAsync(blobKey, stream, ContentTypeFor(blobKey), ct);
    }

    /// <summary>
    /// From the key's extension. The Instagram variant is a JPEG stored beside
    /// the PNG, and Instagram checks the Content-Type it is served with, not
    /// just the bytes.
    /// </summary>
    public static string ContentTypeFor(string blobKey) =>
        blobKey.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || blobKey.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? "image/jpeg"
            : "image/png";

    public Task<Stream> OpenReadAsync(string blobKey, CancellationToken ct) =>
        blobs.DownloadAsync(blobKey, ct);

    public async Task<bool> ExistsAsync(string blobKey, CancellationToken ct)
    {
        try
        {
            await using var stream = await blobs.DownloadAsync(blobKey, ct);
            return true;
        }
        catch (Exception)
        {
            // Any failure is treated as "not there", which is the safe
            // direction: the worst outcome is re-rendering something that
            // already existed. Treating an error as "present" would serve a
            // URL to a blob that is not.
            return false;
        }
    }

    public Uri GetReadOnlyUrl(string blobKey, TimeSpan validFor) =>
        blobs.GetReadOnlyUri(blobKey, validFor);
}
