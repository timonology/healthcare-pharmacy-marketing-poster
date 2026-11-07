namespace Acme.Application.Templates;

/// <param name="BlobKey">Blob key, for deletion and direct server-side reads.</param>
/// <param name="Url">
/// Application-relative URL to put in a canvas `blobKey` field, e.g.
/// "/api/assets/templates/ab12….jpg". Relative rather than absolute because a
/// canvas outlives the host it was authored on — the same template is rendered
/// from localhost, staging and production.
/// </param>
public sealed record StoredTemplateAsset(
    string BlobKey,
    string Url,
    string ContentType);

/// <summary>
/// Storage for images used inside poster templates.
/// </summary>
/// <remarks>
/// Deliberately separate from <c>IBrandAssetService</c>, because the lifetime
/// rules are opposite. Brand logos are handed out as short-lived SAS URLs — an
/// embed that expires is fine, the page just re-fetches. A template image is
/// baked into a canvas document that lives forever, is served to every
/// pharmacy, and is fetched again server-side whenever someone exports a PDF.
/// A SAS URL there would work in testing and then quietly start returning 403
/// weeks later, breaking the design for everyone at once.
///
/// So template assets are addressed by a stable application URL and streamed
/// through the API instead.
/// </remarks>
public interface ITemplateAssetService
{
    Task<StoredTemplateAsset> UploadAsync(
        Stream content,
        string contentType,
        string fileExtension,
        CancellationToken ct);

    /// <summary>Open an asset for streaming, or null when it doesn't exist.</summary>
    Task<Stream?> OpenAsync(string fileName, CancellationToken ct);

    /// <summary>
    /// Maps an asset URL back to its blob key, or null if the value isn't one
    /// of ours. Used by the PDF exporter to read the blob directly instead of
    /// making the API issue an HTTP request to itself.
    /// </summary>
    string? TryResolveBlobKey(string urlOrPath);

    /// <summary>True when the value is an asset URL this service owns.</summary>
    bool IsAssetUrl(string urlOrPath);
}
