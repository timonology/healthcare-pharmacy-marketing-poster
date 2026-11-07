using Acme.Application.Videos;
using Acme.Infrastructure.Storage;

namespace Acme.Infrastructure.VideoPipeline;

/// <summary>
/// Adapts the existing <see cref="IBlobStorageService"/> to the videos-specific
/// <see cref="IVideoStorage"/> interface. Keeps the Application layer unaware of
/// Azure Blob specifics.
/// </summary>
public sealed class AzureBlobVideoStorage(IBlobStorageService blobs) : IVideoStorage
{
    // Render artifacts are uploaded by the Python ai-service worker directly —
    // the .NET side streams no MP4 bytes anywhere. The one thing it does upload
    // is a picture a pharmacist attached in the browser, which never goes near
    // the worker.

    public Task UploadAsync(string blobKey, Stream content, string contentType, CancellationToken ct) =>
        blobs.UploadAsync(blobKey, content, contentType, ct);

    public Task DeleteAsync(string blobKey, CancellationToken ct) =>
        blobs.DeleteAsync(blobKey, ct);

    public async Task DeleteByPrefixAsync(string prefix, CancellationToken ct)
    {
        // Listed then deleted one at a time. A batch delete would be fewer round
        // trips, but this runs once when somebody removes a video and the list
        // is a handful of keys — the MP4, the thumbnail, the narration audio and
        // at most a few stills.
        foreach (var key in await blobs.ListKeysAsync(prefix, ct))
            await blobs.DeleteAsync(key, ct);
    }

    public Task<Uri?> GetReadUrlAsync(string blobKey, TimeSpan validFor, CancellationToken ct)
    {
        Uri? uri = null;
        try { uri = blobs.GetReadOnlyUri(blobKey, validFor); }
        catch { /* return null */ }
        return Task.FromResult(uri);
    }

    public async Task<VideoBlobStream?> OpenReadAsync(string blobKey, CancellationToken ct)
    {
        try
        {
            // Buffer into a MemoryStream. The Azure download stream isn't
            // reliably seekable, and ASP.NET's `enableRangeProcessing: true`
            // (which we set so <video> can seek) requires seek support.
            // Video files are small (15s of 1080p ≈ 6MB) so the memory cost
            // is trivial vs. the reliability win.
            var download = await blobs.DownloadAsync(blobKey, ct);
            var ms = new MemoryStream();
            await download.CopyToAsync(ms, ct);
            ms.Position = 0;

            var contentType = blobKey.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                ? "video/mp4"
                : blobKey.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || blobKey.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                    ? "image/jpeg"
                    : "application/octet-stream";
            return new VideoBlobStream(ms, contentType, ms.Length);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}
