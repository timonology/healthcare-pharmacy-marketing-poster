using Acme.Infrastructure.Options;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Storage;

public sealed class AzureBlobStorageService : IBlobStorageService
{
    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private bool _containerReady;

    public AzureBlobStorageService(IOptions<AzureBlobOptions> options)
    {
        var opts = options.Value;
        var serviceClient = new BlobServiceClient(opts.ConnectionString);
        _container = serviceClient.GetBlobContainerClient(opts.Container);
    }

    public async Task<string> UploadAsync(string blobKey, Stream content, string contentType, CancellationToken ct)
    {
        await EnsureContainerAsync(ct);

        var blob = _container.GetBlobClient(blobKey);
        await blob.UploadAsync(
            content,
            new BlobHttpHeaders { ContentType = contentType },
            cancellationToken: ct);
        return blob.Uri.ToString();
    }

    public async Task<Stream> DownloadAsync(string blobKey, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(blobKey);
        var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
        return response.Value.Content;
    }

    public Task DeleteAsync(string blobKey, CancellationToken ct) =>
        _container.GetBlobClient(blobKey).DeleteIfExistsAsync(cancellationToken: ct);

    public Uri GetReadOnlyUri(string blobKey, TimeSpan validFor)
    {
        var blob = _container.GetBlobClient(blobKey);
        if (!blob.CanGenerateSasUri)
            return blob.Uri;

        var sas = new BlobSasBuilder
        {
            BlobContainerName = _container.Name,
            BlobName = blobKey,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.Add(validFor),
        };
        sas.SetPermissions(BlobSasPermissions.Read);
        return blob.GenerateSasUri(sas);
    }

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_containerReady) return;

        await _ensureLock.WaitAsync(ct);
        try
        {
            if (_containerReady) return;
            await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
            _containerReady = true;
        }
        finally
        {
            _ensureLock.Release();
        }
    }
}
