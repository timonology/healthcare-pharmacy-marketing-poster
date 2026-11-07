using System.Security.Cryptography;
using System.Text;
using Acme.Application.Auth;
using Acme.Application.BrandKit;
using Acme.Application.Common;
using Acme.Application.Export;
using Acme.Application.Subscriptions;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Posters;

/// <summary>
/// Storage for rendered poster images.
/// </summary>
/// <remarks>
/// Its own abstraction rather than reusing the brand-asset or template-asset
/// services, because the lifetime rules differ again. A rendered poster is a
/// derived artefact: it can be thrown away and rebuilt from the canvas at any
/// time, and it becomes wrong the moment the canvas changes. Neither of the
/// others has that property.
/// </remarks>
public interface IPosterImageStore
{
    Task UploadAsync(string blobKey, byte[] png, CancellationToken ct);

    /// <summary>True when this exact render already exists.</summary>
    Task<bool> ExistsAsync(string blobKey, CancellationToken ct);

    /// <summary>Reads a stored render back. Throws when it is not there.</summary>
    Task<Stream> OpenReadAsync(string blobKey, CancellationToken ct);

    /// <summary>A URL the platforms can fetch. Must be reachable from the public internet.</summary>
    Uri GetReadOnlyUrl(string blobKey, TimeSpan validFor);
}

public sealed record RenderedPoster(string BlobKey, string Url, string Name);

/// <summary>A My Posters card image, or "not modified" when the browser's copy is current.</summary>
public sealed record PosterCard(byte[]? Bytes, string ContentType, string ETag, bool NotModified);

/// <summary>
/// Turns a poster into an actual image file, and keeps it.
/// </summary>
/// <remarks>
/// <para>
/// Nothing else in the system stores a rendered poster. The canvas JSON is the
/// source of truth and every view is regenerated from it — previews are SVG
/// drawn in the browser, PDFs are drawn per request. That works until something
/// outside the product needs a file: Facebook and Instagram fetch media by URL
/// and cannot be handed a JSON document.
/// </para>
/// <para>
/// <b>The blob key contains a hash of the canvas.</b> That is what makes the
/// cache correct rather than merely fast: an edited poster produces a different
/// key, so a stale image can never be served, and re-rendering the same canvas
/// costs one existence check. Storing a fixed key per poster would have meant
/// tracking staleness separately and getting it wrong the first time someone
/// edited a poster after publishing it.
/// </para>
/// </remarks>
public sealed class PosterImageService(
    IPosterRepository posters,
    IUserRepository users,
    ICanvasRasterizer rasterizer,
    IPosterImageStore store,
    ILogger<PosterImageService> log,
    // Read only for the logo's blob key, so a canvas reference can be
    // turned into bytes the renderer can actually load.
    IBrandKitRepository brandKits,
    // Encodes the booking QR at render time. Every surface that shows a poster
    // — the campaign email, a shop-window screen, a PDF export — comes through
    // EnsureRenderedAsync, so wiring it here is what puts the code on all of
    // them rather than on whichever one was remembered.
    IQrCodeGenerator qrCodes,
    // Builds the Instagram feed version from the cached PNG.
    IImageTranscoder transcoder)
{
    /// <summary>
    /// Render width. 1240px is the canvas's own width for A4 at 150dpi, and
    /// comfortably above Instagram's 1080px feed width — so the platforms
    /// downscale rather than upscale, which is the direction that looks good.
    /// </summary>
    private const int RenderWidthPx = 1240;

    /// <summary>
    /// Long enough for Instagram to ingest, short enough to be useless by the
    /// time it appears in a log.
    /// </summary>
    private static readonly TimeSpan UrlValidity = TimeSpan.FromHours(2);

    /// <summary>
    /// Returns a rendered image for the poster, rendering it if needed.
    /// </summary>
    /// <remarks>
    /// Idempotent and safe to call on every publish attempt. A retry after a
    /// failure re-uses the existing render rather than paying for it twice.
    /// </remarks>
    public async Task<Result<RenderedPoster>> EnsureRenderedAsync(
        string posterId, string ownerId, CancellationToken ct)
    {
        if (await ResolveAsync(posterId, ownerId, ct) is not { } resolved)
            return Result<RenderedPoster>.NotFound("Poster not found.");

        var (poster, canvasJson, watermark) = resolved;
        var key = BuildKey(ownerId, poster.Id, canvasJson, watermark);

        if (await store.ExistsAsync(key, ct))
        {
            // Recorded on the poster even on a cache hit — the stored key may
            // be an older render, and this is the cheap moment to correct it.
            if (poster.ThumbnailBlobKey != key)
            {
                poster.SetThumbnail(key);
                await posters.UpdateAsync(poster, ct);
            }

            return Result<RenderedPoster>.Success(
                new RenderedPoster(key, store.GetReadOnlyUrl(key, UrlValidity).ToString(), poster.Name));
        }

        byte[] png;
        try
        {
            png = await rasterizer.RenderPngAsync(
                new RasterRequest(canvasJson, RenderWidthPx, watermark), ct);
        }
        catch (Exception ex)
        {
            // Surfaced rather than swallowed. A poster that cannot be rendered
            // will fail to publish anyway, and "rendering failed" is a far more
            // actionable message than "that poster no longer exists".
            log.LogError(ex, "Rendering poster {Poster} to PNG failed", poster.Id);
            return Result<RenderedPoster>.Invalid(
                "This poster could not be turned into an image. Open it in the designer and re-save it.");
        }

        if (png.Length == 0)
            return Result<RenderedPoster>.Invalid("This poster rendered as an empty image.");

        await store.UploadAsync(key, png, ct);

        poster.SetThumbnail(key);
        await posters.UpdateAsync(poster, ct);

        log.LogInformation(
            "Rendered poster {Poster} to {Key} ({Bytes} bytes)", poster.Id, key, png.Length);

        return Result<RenderedPoster>.Success(
            new RenderedPoster(key, store.GetReadOnlyUrl(key, UrlValidity).ToString(), poster.Name));
    }

    /// <summary>
    /// The poster as an Instagram feed image: a 1080×1350 JPEG.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Instagram refuses PNG and refuses anything taller than 4:5, and a poster
    /// is an A4 PNG — so without this every poster sent to Instagram fails.
    /// See <see cref="IImageTranscoder.ToInstagramJpeg"/> for how it is fitted.
    /// </para>
    /// <para>
    /// Derived from the cached PNG and stored next to it under the same hash,
    /// so it is rebuilt exactly when the poster changes and costs one existence
    /// check otherwise.
    /// </para>
    /// </remarks>
    public async Task<Result<RenderedPoster>> EnsureInstagramVariantAsync(
        string posterId, string ownerId, CancellationToken ct)
    {
        var rendered = await EnsureRenderedAsync(posterId, ownerId, ct);
        if (rendered.Kind != ResultKind.Success) return rendered;

        var png = rendered.Value!;
        var key = InstagramKey(png.BlobKey);

        if (!await store.ExistsAsync(key, ct))
        {
            byte[] source;
            await using (var stream = await store.OpenReadAsync(png.BlobKey, ct))
            using (var buffer = new MemoryStream())
            {
                await stream.CopyToAsync(buffer, ct);
                source = buffer.ToArray();
            }

            var jpeg = transcoder.ToInstagramJpeg(source);
            if (jpeg is null || jpeg.Length == 0)
                return Result<RenderedPoster>.Invalid(
                    "The Instagram version of this poster could not be prepared.");

            await store.UploadAsync(key, jpeg, ct);
            log.LogInformation(
                "Built Instagram version of poster {Poster} at {Key} ({Bytes} bytes)",
                posterId, key, jpeg.Length);
        }

        return Result<RenderedPoster>.Success(
            new RenderedPoster(key, store.GetReadOnlyUrl(key, UrlValidity).ToString(), png.Name));
    }

    /// <summary>
    /// The poster and the exact canvas every renderer is given: owner checked,
    /// brand assets and details filled in, watermark decided by plan.
    /// </summary>
    private async Task<(Acme.Domain.Posters.Poster Poster, string CanvasJson, bool Watermark)?> ResolveAsync(
        string posterId, string ownerId, CancellationToken ct)
    {
        var poster = await posters.FindByIdAsync(posterId, ct);
        if (poster is null || poster.OwnerId != ownerId) return null;

        // Free-tier posters carry the watermark wherever they end up, which
        // includes somebody's Facebook page. Read from the plan rather than
        // from the request so a caller cannot ask for it to be left off.
        var user = await users.FindByIdAsync(ownerId, ct);
        var watermark = user is not null && Plans.For(user.Tier).Watermark;

        // Resolved *before* the cache key is computed, and that order matters.
        //
        // The key is a hash of the canvas, so resolving first means uploading a
        // new logo changes the canvas the renderer sees, which changes the key,
        // which re-renders every poster that uses it. Hashing the unresolved
        // canvas instead would serve the old logo from cache forever, and the
        // only symptom would be a pharmacy insisting their new logo "didn't
        // save" while the brand-kit page showed it perfectly.
        var kit = await brandKits.FindByOwnerAsync(ownerId, ct);

        // The profile goes in for the same reason the logo does, and is part of
        // the same cache key: a template's contact line is written as
        // {{address}} tokens, so a poster rendered without the owner's details
        // would print the tokens themselves. Editing your phone number changes
        // the resolved canvas, which changes the key, which re-renders.
        var canvasJson = BrandCanvasAssets.ResolveForRender(
            poster.CanvasJson, kit, user?.Profile, qrCodes);

        return (poster, canvasJson, watermark);
    }

    // ── the My Posters card ──────────────────────────────────────────────────

    /// <summary>Card render width: about 2× the widest card on a desktop grid.</summary>
    public const int CardWidthPx = 600;

    /// <summary>
    /// Bumped when the card's rendering changes (size, encoding), so every
    /// stored card is rebuilt rather than served as it was.
    /// </summary>
    private const string CardVersion = "c1";

    /// <summary>
    /// At most this many card renders at once, across the process. A first
    /// visit to My Posters after a deploy asks for every card on the page at
    /// once; unbounded, that is fifty Skia surfaces and fifty sets of image
    /// downloads competing with exports and campaign sends.
    /// </summary>
    private static readonly SemaphoreSlim CardRenders = new(2, 2);

    /// <summary>
    /// The poster's card image for the My Posters grid, drawn by the same
    /// renderer as its PDF and PNG.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not the stored thumbnail.</b> Cards used to show
    /// <c>ThumbnailBlobKey</c> through a signed storage URL. That key is
    /// whatever was last rendered for publishing, so it was missing for most
    /// posters, stale for any edited since, and the URL pointed the browser
    /// straight at storage — which in Docker is <c>azurite:10000</c>, a host
    /// the browser cannot resolve, and in production expired after fifteen
    /// minutes. The result was a broken-image "?" on exactly the posters that
    /// had been published.
    /// </para>
    /// <para>
    /// <b>Regenerated on next view, no backfill.</b> The key hashes the
    /// resolved canvas (logo, details, booking QR and watermark included), so
    /// an edited poster, a new logo or a plan change produces a new key and
    /// the next view renders it. Nothing stale can be served, and there is no
    /// deploy-time job rendering every poster ever made for people who may
    /// never look at them.
    /// </para>
    /// <para>
    /// <b>Never writes to the poster.</b> Viewing a list must not change
    /// <c>UpdatedAtUtc</c> — the list is ordered by it.
    /// </para>
    /// </remarks>
    /// <param name="ifNoneMatch">
    /// The browser's cached ETag, if any. When it still matches, the answer is
    /// "not modified" without touching storage.
    /// </param>
    public async Task<Result<PosterCard>> EnsureCardAsync(
        string posterId, string ownerId, string? ifNoneMatch, CancellationToken ct)
    {
        if (await ResolveAsync(posterId, ownerId, ct) is not { } resolved)
            return Result<PosterCard>.NotFound("Poster not found.");

        var (poster, canvasJson, watermark) = resolved;
        var key = CardKey(BuildKey(ownerId, poster.Id, canvasJson, watermark));
        var etag = CardETag(key);

        // Weak comparison, as RFC 9110 asks of If-None-Match: a proxy that
        // compresses a response may hand the browser W/"…" for our "…".
        if (!string.IsNullOrEmpty(ifNoneMatch)
            && ifNoneMatch.Split(',').Select(t => t.Trim()).Any(t =>
                t == "*" || t == etag || t == "W/" + etag))
            return Result<PosterCard>.Success(new PosterCard(null, CardContentType, etag, NotModified: true));

        if (await TryReadAsync(key, ct) is { } cached)
            return Result<PosterCard>.Success(new PosterCard(cached, CardContentType, etag, NotModified: false));

        await CardRenders.WaitAsync(ct);
        try
        {
            // Another request for the same card may have finished while this
            // one queued; reading is far cheaper than rendering again.
            if (await TryReadAsync(key, ct) is { } justMade)
                return Result<PosterCard>.Success(new PosterCard(justMade, CardContentType, etag, NotModified: false));

            byte[] card;
            try
            {
                card = await RenderCardAsync(rasterizer, transcoder, canvasJson, watermark, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Rendering the card for poster {Poster} failed", poster.Id);
                return Result<PosterCard>.Invalid("This poster's preview could not be drawn.");
            }

            try
            {
                await store.UploadAsync(key, card, ct);
                log.LogInformation(
                    "Rendered card for poster {Poster} at {Key} ({Bytes} bytes)", poster.Id, key, card.Length);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Storage refusing the cache copy is no reason to withhold the
                // card that has just been drawn: serve it, render again next time.
                log.LogWarning(ex, "Could not store the card for poster {Poster}; serving it uncached", poster.Id);
            }

            return Result<PosterCard>.Success(new PosterCard(card, CardContentType, etag, NotModified: false));
        }
        finally
        {
            CardRenders.Release();
        }
    }

    private const string CardContentType = "image/jpeg";

    /// <summary>
    /// Draws a card: the PDF/PNG renderer at <see cref="CardWidthPx"/>, encoded
    /// as JPEG.
    /// </summary>
    /// <remarks>
    /// Public and static so a test can run it on a real canvas with the real
    /// renderer and nothing else. Throws rather than returning an empty image:
    /// an empty card is the bug this exists to prevent, and the caller turns a
    /// throw into "no image", which the page draws its own preview for.
    /// </remarks>
    public static async Task<byte[]> RenderCardAsync(
        ICanvasRasterizer rasterizer, IImageTranscoder transcoder,
        string resolvedCanvasJson, bool watermark, CancellationToken ct)
    {
        var png = await rasterizer.RenderPngAsync(
            new RasterRequest(resolvedCanvasJson, CardWidthPx, watermark), ct);
        if (png.Length == 0)
            throw new InvalidOperationException("The renderer returned an empty image.");

        var (width, height) = CanvasSize(resolvedCanvasJson);
        var cardHeight = Math.Max(1, (int)Math.Round(height * (CardWidthPx / width)));

        // A frame of exactly the rendered size: re-encoded, never letterboxed.
        var jpeg = transcoder.FitToFrameJpeg(png, CardWidthPx, cardHeight, whiteBackground: true);
        if (jpeg is null || jpeg.Length == 0)
            throw new InvalidOperationException("The rendered card could not be encoded.");

        return jpeg;
    }

    /// <summary>The canvas's own size, as the renderer reads it.</summary>
    private static (float Width, float Height) CanvasSize(string canvasJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(canvasJson);
            var root = doc.RootElement;
            float Read(string name, float fallback) =>
                root.TryGetProperty(name, out var v)
                && v.ValueKind == System.Text.Json.JsonValueKind.Number
                && v.TryGetSingle(out var f) && f > 0 ? f : fallback;
            return (Read("width", 1240), Read("height", 1754));
        }
        catch (System.Text.Json.JsonException)
        {
            return (1240, 1754);
        }
    }

    /// <summary>The card sits beside the full render it shares a hash with.</summary>
    public static string CardKey(string pngKey) =>
        (pngKey.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? pngKey[..^4] : pngKey)
        + $"-card-{CardVersion}.jpg";

    /// <summary>
    /// A strong ETag for a card. The key already changes with the content
    /// (canvas hash, watermark, card version), so a hash of the key does too.
    /// </summary>
    public static string CardETag(string cardKey) =>
        "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cardKey)))[..20].ToLowerInvariant() + "\"";

    private async Task<byte[]?> TryReadAsync(string key, CancellationToken ct)
    {
        try
        {
            await using var stream = await store.OpenReadAsync(key, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            return buffer.Length > 0 ? buffer.ToArray() : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Not there yet (or storage refused): render instead.
            return null;
        }
    }

    /// <summary>The Instagram variant sits beside the PNG it came from.</summary>
    public static string InstagramKey(string pngKey) =>
        pngKey.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? pngKey[..^4] + "-ig.jpg"
            : pngKey + "-ig.jpg";

    /// <summary>
    /// A key that changes whenever the rendered result would change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hash covers the canvas <i>and</i> the watermark flag, because the
    /// same canvas renders differently on Free and on Starter. Leaving the flag
    /// out would mean an upgrading pharmacy kept serving the watermarked image
    /// they had already paid to remove.
    /// </para>
    /// <para>
    /// SHA-256 truncated to 16 hex characters. Collisions at 64 bits are not a
    /// realistic concern for one pharmacy's posters, and a shorter key keeps
    /// the URL readable in logs.
    /// </para>
    /// <para>
    /// Public rather than internal because it is the description of the blob
    /// layout, and because this codebase tests helpers directly rather than
    /// reaching through <c>InternalsVisibleTo</c> — the same choice
    /// <c>ContactMask</c> and <c>VideoModels</c> already make.
    /// </para>
    /// </remarks>
    public static string BuildKey(string ownerId, string posterId, string canvasJson, bool watermark)
    {
        var payload = Encoding.UTF8.GetBytes($"{canvasJson}|wm={watermark}");
        var hash = Convert.ToHexString(SHA256.HashData(payload))[..16].ToLowerInvariant();

        return $"posters/{ownerId}/{posterId}-{hash}.png";
    }
}
