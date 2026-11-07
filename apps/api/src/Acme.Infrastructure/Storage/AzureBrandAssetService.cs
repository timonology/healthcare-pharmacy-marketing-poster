using Acme.Application.BrandKit;
using Acme.Application.Templates;
using Acme.Domain.Common;
using Acme.Infrastructure.Imaging;

namespace Acme.Infrastructure.Storage;

public sealed class AzureBrandAssetService(IBlobStorageService blobs) : IBrandAssetService
{
    private const string LogoPrefix = "brand/logos";

    /// <summary>Largest logo accepted, matching the controller's request limit.</summary>
    private const int MaxLogoBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Stores a logo by what its bytes are, not what the browser called it
    /// (security review SEC-10).
    /// </summary>
    /// <remarks>
    /// PNG, JPEG and WebP are stored as they are, under the type and extension
    /// their bytes prove. An SVG is drawn to PNG first, so no script-capable
    /// document is ever stored or served. Anything else is refused. The
    /// declared type and file name are ignored: both are the client's say-so.
    /// </remarks>
    public async Task<string> UploadLogoAsync(
        string ownerId,
        Stream content,
        string contentType,
        string fileExtension,
        CancellationToken ct)
    {
        var bytes = await ReadCappedAsync(content, ct);

        (string? type, string? ext) = ImageSniffer.Detect(bytes) switch
        {
            "image/png" => ("image/png", "png"),
            "image/jpeg" => ("image/jpeg", "jpg"),
            "image/webp" => ("image/webp", "webp"),
            _ => (null, null),
        };

        if (type is null)
        {
            if (!SvgRasterizer.LooksLikeSvg(bytes))
                throw new DomainException("Upload the logo as a PNG, JPEG, WebP or SVG image.");

            byte[]? png;
            try { png = SvgRasterizer.ToPng(bytes); }
            catch (Exception) { png = null; }
            bytes = png ?? throw new DomainException(
                "That SVG could not be used — it links to files outside itself, or could not be read. Export the logo as a PNG and upload that instead.");
            (type, ext) = ("image/png", "png");
        }

        var blobKey = $"{LogoPrefix}/{ownerId}/{Guid.NewGuid():N}.{ext}";
        using var stream = new MemoryStream(bytes, writable: false);
        await blobs.UploadAsync(blobKey, stream, type, ct);
        return blobKey;
    }

    private static async Task<byte[]> ReadCappedAsync(Stream content, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > MaxLogoBytes)
                throw new DomainException($"The logo is larger than {MaxLogoBytes / (1024 * 1024)}MB.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    public Task DeleteLogoAsync(string blobKey, CancellationToken ct) =>
        blobs.DeleteAsync(blobKey, ct);

    public Uri GetReadOnlyUrl(string blobKey, TimeSpan validFor) =>
        blobs.GetReadOnlyUri(blobKey, validFor);

    public async Task<Stream?> OpenLogoAsync(string blobKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(blobKey)) return null;
        try
        {
            return await blobs.DownloadAsync(blobKey, ct);
        }
        catch (Exception)
        {
            // A missing blob is a logo that was deleted out from under the kit,
            // not a server fault. The caller turns this into a 404 and the page
            // shows no logo, which is the truth.
            return null;
        }
    }
}
