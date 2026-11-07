namespace Acme.Infrastructure.Storage;

public interface IBlobStorageService
{
    Task<string> UploadAsync(string blobKey, Stream content, string contentType, CancellationToken ct);
    Task<Stream> DownloadAsync(string blobKey, CancellationToken ct);
    Task DeleteAsync(string blobKey, CancellationToken ct);

    /// <summary>Every blob key beginning with <paramref name="prefix"/>.</summary>
    /// <remarks>
    /// Added so a caller can clean up everything it owns without having to
    /// remember each key it ever wrote — the failure mode being a new artifact
    /// that quietly accumulates in the container for every record ever created.
    /// </remarks>
    Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct);
    Uri GetReadOnlyUri(string blobKey, TimeSpan validFor);
}
