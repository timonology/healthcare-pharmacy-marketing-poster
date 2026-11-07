using System.Collections.Concurrent;
using Acme.Application.Videos;

namespace Acme.IntegrationTests;

/// <summary>
/// Keeps video files in a dictionary instead of in blob storage.
/// </summary>
/// <remarks>
/// Same reason as <see cref="InMemoryPosterImageStore"/>: the real store talks
/// to Azure (Azurite locally), which a machine that has only checked the code
/// out is not running. Only the storage is replaced — the signed-link check and
/// the range handling in front of it are the product's own.
/// </remarks>
public sealed class InMemoryVideoStorage : IVideoStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new();

    public void Put(string blobKey, byte[] bytes) => _files[blobKey] = bytes;

    public Task DeleteAsync(string blobKey, CancellationToken ct)
    {
        _files.TryRemove(blobKey, out _);
        return Task.CompletedTask;
    }

    public async Task UploadAsync(string blobKey, Stream content, string contentType, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        _files[blobKey] = buffer.ToArray();
    }

    public Task DeleteByPrefixAsync(string prefix, CancellationToken ct)
    {
        foreach (var key in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            _files.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<Uri?> GetReadUrlAsync(string blobKey, TimeSpan validFor, CancellationToken ct) =>
        Task.FromResult<Uri?>(null);

    public Task<VideoBlobStream?> OpenReadAsync(string blobKey, CancellationToken ct) =>
        Task.FromResult(_files.TryGetValue(blobKey, out var bytes)
            ? new VideoBlobStream(new MemoryStream(bytes, writable: false), "video/mp4", bytes.Length)
            : null);
}
