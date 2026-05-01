namespace Acme.Infrastructure.Storage;

public interface IBlobStorageService
{
    Task<string> UploadAsync(string blobKey, Stream content, string contentType, CancellationToken ct);
    Task<Stream> DownloadAsync(string blobKey, CancellationToken ct);
    Task DeleteAsync(string blobKey, CancellationToken ct);
    Uri GetReadOnlyUri(string blobKey, TimeSpan validFor);
}
