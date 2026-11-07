using System.Text.RegularExpressions;
using Acme.Application.Ai;

namespace Acme.Infrastructure.Storage;

/// <summary>
/// <see cref="IPharmacyPhotoStore"/> on blob storage, under
/// <c>pharmacy-photos/{ownerId}/</c> (security review SEC-15). Nothing serves
/// this prefix anonymously.
/// </summary>
public sealed class BlobPharmacyPhotoStore(IBlobStorageService blobs) : IPharmacyPhotoStore
{
    private const string Root = "pharmacy-photos";

    private static readonly Regex OwnerPattern = new(
        "^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string Prefix(string ownerId)
    {
        // The owner id comes from a validated token, but it still becomes part
        // of a storage path, so it is held to the shape ids actually have.
        if (!OwnerPattern.IsMatch(ownerId))
            throw new ArgumentException("Not a valid owner id.", nameof(ownerId));
        return $"{Root}/{ownerId}/";
    }

    public async Task SaveAsync(string ownerId, string fileName, Stream content, string contentType, CancellationToken ct)
    {
        if (!PharmacyPhotos.IsValidFileName(fileName))
            throw new ArgumentException("Not a valid upload name.", nameof(fileName));
        await blobs.UploadAsync(Prefix(ownerId) + fileName, content, contentType, ct);
    }

    public async Task<Stream?> OpenAsync(string ownerId, string fileName, CancellationToken ct)
    {
        if (!PharmacyPhotos.IsValidFileName(fileName) || !OwnerPattern.IsMatch(ownerId)) return null;
        try
        {
            return await blobs.DownloadAsync(Prefix(ownerId) + fileName, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Missing is the expected failure — an expired upload — and the
            // caller says "upload it again" either way.
            return null;
        }
    }

    public async Task<int> DeleteOlderThanAsync(string ownerId, DateTime cutoffUtc, CancellationToken ct)
    {
        var prefix = Prefix(ownerId);
        var deleted = 0;
        foreach (var key in await blobs.ListKeysAsync(prefix, ct))
        {
            var name = key[(key.LastIndexOf('/') + 1)..];
            if (PharmacyPhotos.DateOf(name) is { } day && day < cutoffUtc.Date)
            {
                await blobs.DeleteAsync(key, ct);
                deleted++;
            }
        }
        return deleted;
    }
}
