using System.Collections.Concurrent;
using Acme.Application.Posters;

namespace Acme.IntegrationTests;

/// <summary>
/// Keeps rendered posters in a dictionary instead of in blob storage.
/// </summary>
/// <remarks>
/// <para>
/// The only external dependency substituted in the poster path, and only this
/// one. The rasterizer stays real: <c>QuestPdfExporter</c> runs in-process, so
/// the PNG a test inspects is the PNG the product would have produced. What is
/// replaced is <c>AzurePosterImageStore</c>, which talks to Azure — configured
/// as <c>UseDevelopmentStorage=true</c>, meaning an Azurite emulator that is
/// not running on a machine that has only checked the code out.
/// </para>
/// <para>
/// This matters more than a normal convenience fake, because
/// <c>CampaignService</c> deliberately swallows a render failure: a campaign
/// whose poster cannot be produced still goes out, carrying the text and the
/// video links. That is the right behaviour for a recipient and the wrong
/// behaviour for a test, which would see an email with no inline image and
/// report a missing poster — indistinguishable from the regression it exists
/// to catch. Substituting the store removes the ambiguity: if the image is
/// absent now, the code lost it.
/// </para>
/// </remarks>
public sealed class InMemoryPosterImageStore : IPosterImageStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    /// <summary>How many distinct renders were stored. Used to assert caching.</summary>
    public int Count => _blobs.Count;

    public Task UploadAsync(string blobKey, byte[] png, CancellationToken ct)
    {
        _blobs[blobKey] = png;
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string blobKey, CancellationToken ct) =>
        Task.FromResult(_blobs.ContainsKey(blobKey));

    public Task<Stream> OpenReadAsync(string blobKey, CancellationToken ct)
    {
        if (!_blobs.TryGetValue(blobKey, out var bytes))
            throw new InvalidOperationException($"No render stored under '{blobKey}'.");

        // A fresh stream per call: the caller disposes it, and a shared one
        // would be readable exactly once.
        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public Uri GetReadOnlyUrl(string blobKey, TimeSpan validFor) =>
        new($"https://posters.invalid/{Uri.EscapeDataString(blobKey)}");
}
