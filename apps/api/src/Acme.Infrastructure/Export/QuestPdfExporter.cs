using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Acme.Application.Export;
using Acme.Infrastructure.Imaging;
using Acme.Infrastructure.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;
using Svg.Skia;

namespace Acme.Infrastructure.Export;

/// <summary>
/// Renders a canvas document to PDF (for print and email) and to PNG (for the
/// social platforms).
/// </summary>
/// <remarks>
/// One class, two interfaces, deliberately. The hard part of both jobs is the
/// same — parse the canvas, fetch every referenced image, and interleave
/// bitmaps with vector layers in z-order — and a second implementation would
/// drift from this one within a release or two, so the same poster would print
/// differently from how it posts. The output stage is the only part that
/// differs.
/// </remarks>
public sealed class QuestPdfExporter : IPosterPdfExporter, ICanvasRasterizer
{
    private const string BrandName = "Marketing25";
    private const string BrandWatermark = "Made with Marketing25 · marketing25.co.uk";

    /// <summary>Cache image bytes so a poster sent to 200 recipients only downloads each image once.</summary>
    /// <summary>
    /// Fetched and decoded images, shared across renders — bounded.
    /// </summary>
    /// <remarks>
    /// It was an unbounded dictionary that was never emptied, so every distinct
    /// image source ever rendered stayed in memory for the life of the process:
    /// a poster pointing at many different or very large images could grow the
    /// API until it was killed (security review SEC-07). Now capped by total
    /// bytes, with a sliding expiry.
    /// </remarks>
    private static readonly MemoryCache _cache = new(new MemoryCacheOptions
    {
        SizeLimit = 96L * 1024 * 1024,
    });

    /// <summary>The most a single image may weigh, whatever its source.</summary>
    internal const long MaxImageBytes = 12L * 1024 * 1024;

    private static readonly MemoryCacheEntryOptions CacheEntry = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(30),
    };

    /// <summary>Reused HttpClient — thread-safe and cheap to keep alive.</summary>
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        // A redirect is a second, unchecked destination: an allowed host could
        // send the request anywhere. Allowed sources never need one.
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly IBlobStorageService? _blobs;
    private readonly ILogger<QuestPdfExporter> _logger;

    static QuestPdfExporter()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public QuestPdfExporter(ILogger<QuestPdfExporter> logger, IBlobStorageService? blobs = null)
    {
        _logger = logger;
        _blobs = blobs;
    }

    public async Task<byte[]> RenderAsync(PdfExportRequest request, CancellationToken ct)
    {
        var canvas = ParseCanvas(request.CanvasJson);
        var imageShapeCount = canvas.Shapes.Count(s => s.Kind == "image");
        _logger.LogInformation(
            "PDF export starting: shapes={ShapeCount} images={ImageCount}",
            canvas.Shapes.Count, imageShapeCount);

        var imageData = await PrefetchImagesAsync(canvas, ct);
        _logger.LogInformation(
            "PDF export: fetched {Fetched}/{Requested} image(s)",
            imageData.Count, imageShapeCount);

        // SVG is used for the vector layer (rects, circles, lines, text).
        // Images are overlaid using QuestPDF's Image() because SkiaSharp's SVG
        // parser silently drops <image href="data:…"/> tags.
        // The page size follows the canvas rather than being hardcoded to A4 —
        // the designer now lets users pick A4 or A3 in either orientation, and
        // printing an A3 design onto A4 silently loses a third of the artwork's
        // physical size (and any bleed the printer expects).
        var pageSize = ResolvePageSize(canvas);
        _logger.LogInformation(
            "PDF export: canvas {W}x{H}px -> page {PW}x{PH}pt",
            canvas.Width, canvas.Height, pageSize.Width, pageSize.Height);

        var xScale = pageSize.Width / canvas.Width;   // points per canvas unit (x)
        var yScale = pageSize.Height / canvas.Height; // points per canvas unit (y)

        // Bitmaps can't live inside the SVG (SkiaSharp's parser drops
        // <image href="data:…">), so they're painted as separate QuestPDF
        // layers. That used to mean *every* image sat above *all* vectors,
        // which silently broke any design that puts text over a photo: the
        // photo was painted last and covered the copy.
        //
        // Instead the shapes are split into z-ordered bands — runs of vectors
        // between images — and emitted as alternating layers, so a scrim or a
        // headline with a higher zIndex than the photo lands on top of it.
        var bands = BuildLayerBands(canvas, imageData, request.IncludeWatermark);

        var pdf = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(pageSize);
                page.Margin(0);
                page.PageColor(canvas.Background);

                page.Content().Layers(layers =>
                {
                    // A full-size transparent SVG establishes the page box.
                    // Using the first band would work only when that band is
                    // full-bleed, which isn't guaranteed.
                    layers.PrimaryLayer().Svg(EmptyCanvasSvg(canvas));

                    foreach (var band in bands)
                    {
                        if (band.Svg is { } bandSvg)
                        {
                            layers.Layer().Svg(bandSvg);
                            continue;
                        }

                        var shape = band.Image!;
                        if (!imageData.TryGetValue(shape.BlobKey!, out var img))
                            continue;

                        layers.Layer()
                            .AlignLeft()
                            .AlignTop()
                            .TranslateX(shape.Position.X * xScale)
                            .TranslateY(shape.Position.Y * yScale)
                            .Width(Math.Max(1, shape.Width * xScale))
                            .Height(Math.Max(1, shape.Height * yScale))
                            .Image(img.Bytes)
                            .FitUnproportionally();
                    }
                });
            });
        }).GeneratePdf();

        return pdf;
    }

    /// <summary>
    /// Renders the canvas to a PNG.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors <see cref="RenderAsync"/> exactly up to the output stage — same
    /// parse, same image prefetch, same z-ordered bands — so a poster's PNG and
    /// its PDF are the same picture. Only the final compositing differs: Skia
    /// draws onto a bitmap surface instead of QuestPDF laying out a page.
    /// </para>
    /// <para>
    /// Bitmaps are drawn separately from the vector layers for the reason
    /// documented on <see cref="RenderAsync"/>: SkiaSharp's SVG parser silently
    /// drops <c>&lt;image href="data:…"&gt;</c>, so embedding them in the SVG
    /// produces a poster with holes where every photograph should be.
    /// </para>
    /// </remarks>
    public async Task<byte[]> RenderPngAsync(RasterRequest request, CancellationToken ct)
    {
        var canvas = ParseCanvas(request.CanvasJson);
        var imageData = await PrefetchImagesAsync(canvas, ct);
        var bands = BuildLayerBands(canvas, imageData, request.IncludeWatermark);

        // Clamped rather than trusted. The lower bound keeps text legible; the
        // upper bound stops a caller asking for something that would allocate
        // gigabytes — at 4096 wide an A4 canvas is already a 23-megapixel
        // surface.
        var targetWidth = Math.Clamp(
            request.TargetWidthPx ?? (int)Math.Round(canvas.Width), 320, 4096);

        var scale = targetWidth / canvas.Width;
        var targetHeight = Math.Max(1, (int)Math.Round(canvas.Height * scale));

        _logger.LogInformation(
            "PNG render: canvas {W}x{H} -> {TW}x{TH} ({Bands} band(s), {Images} image(s))",
            canvas.Width, canvas.Height, targetWidth, targetHeight, bands.Count, imageData.Count);

        var info = new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var skia = surface.Canvas;

        // Opaque white when the canvas background will not parse. A
        // transparent PNG posted to Facebook renders on whatever the feed
        // happens to use, which for dark mode means white text on white.
        skia.Clear(SKColor.TryParse(canvas.Background, out var bg) ? bg : SKColors.White);

        foreach (var band in bands)
        {
            ct.ThrowIfCancellationRequested();

            if (band.Svg is { } svgText)
            {
                DrawSvgBand(skia, svgText, scale);
                continue;
            }

            var shape = band.Image!;
            if (shape.BlobKey is null || !imageData.TryGetValue(shape.BlobKey, out var img))
                continue;

            using var bitmap = TryDecode(img.Bytes, shape.BlobKey);
            if (bitmap is null) continue;

            // Position and size only, matching the PDF path. Neither applies
            // rotation to bitmaps, so a rotated photo renders unrotated in
            // both — wrong in the same way rather than differently.
            var dest = SKRect.Create(
                shape.Position.X * scale,
                shape.Position.Y * scale,
                Math.Max(1f, shape.Width * scale),
                Math.Max(1f, shape.Height * scale));

            // No SKPaint: at the default render width the scale is exactly 1.0,
            // so resampling quality is moot, and SkiaSharp has moved
            // FilterQuality between versions. Not worth a build break for a
            // difference nobody can see.
            skia.DrawBitmap(bitmap, dest);
        }

        skia.Flush();

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        var bytes = encoded.ToArray();
        _logger.LogInformation("PNG render complete: {Bytes} bytes", bytes.Length);
        return bytes;
    }

    /// <summary>Rasterises one vector band onto the surface.</summary>
    /// <remarks>
    /// Reading <c>Picture</c> after <c>Load</c> rather than using the return
    /// value, because Svg.Skia has changed that signature between versions
    /// while the property has stayed put.
    /// </remarks>
    private void DrawSvgBand(SKCanvas skia, string svgText, float scale)
    {
        using var svg = new SKSvg();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svgText));

        svg.Load(stream);

        if (svg.Picture is not { } picture)
        {
            _logger.LogWarning("PNG render: a vector layer failed to parse and was skipped");
            return;
        }

        var matrix = SKMatrix.CreateScale(scale, scale);
        skia.DrawPicture(picture, ref matrix);
    }

    /// <summary>
    /// Concurrently download every unique image referenced by an image shape.
    /// Returns raw bytes so QuestPDF's native <c>Image()</c> can consume them
    /// directly — much more reliable than embedding as base64 in SVG.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, FetchedImage>> PrefetchImagesAsync(
        CanvasModel canvas, CancellationToken ct)
    {
        var keys = canvas.Shapes
            .Where(s => s.Kind == "image" && !string.IsNullOrWhiteSpace(s.BlobKey))
            .Select(s => s.BlobKey!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (keys.Count == 0)
            return new Dictionary<string, FetchedImage>();

        var results = new ConcurrentDictionary<string, FetchedImage>();
        await Parallel.ForEachAsync(keys,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
            async (key, token) =>
            {
                try
                {
                    var (mime, bytes) = await LoadImageAsync(key, token);
                    results[key] = new FetchedImage(mime, bytes);
                    _logger.LogInformation("Fetched poster image {Key} ({Bytes} bytes)", key, bytes.Length);
                }
                catch (Exception ex)
                {
                    // Error, not Warning: a poster silently missing its hero
                    // image still gets emailed to real patients, so this is a
                    // defect in a deliverable rather than a nuisance. The
                    // message names the route taken, because "could not fetch"
                    // is the same text whether the blob is absent, the URL was
                    // unrecognised, or the network refused us — and those need
                    // completely different fixes.
                    _logger.LogError(ex,
                        "Poster image dropped: key='{Key}' route={Route} resolved='{Resolved}'. "
                        + "The PDF will render without it.",
                        key, DescribeRoute(key), TemplateAssetUrls.TryResolveBlobKey(key) ?? "(n/a)");
                }
            });
        return results;
    }

    private readonly record struct FetchedImage(string Mime, byte[] Bytes);

    /// <summary>
    /// Which branch of <see cref="LoadImageAsync"/> a key will take.
    /// </summary>
    /// <remarks>
    /// Diagnostic only. Kept in step with the branch order below by hand — it
    /// exists so a failure log says whether the key was treated as a stored
    /// asset, a remote URL, an inline data URI, or a bare blob key, which is
    /// the first question worth answering when an image goes missing.
    /// </remarks>
    private string DescribeRoute(string blobKey)
    {
        if (TemplateAssetUrls.TryResolveBlobKey(blobKey) is not null)
            return _blobs is not null ? "template-asset/blob" : "template-asset/NO-BLOB-SERVICE";
        if (blobKey.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || blobKey.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "remote-url";
        if (blobKey.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return "data-uri";
        return _blobs is not null ? "bare-blob-key" : "UNRESOLVABLE";
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImageProbe>> ProbeImagesAsync(
        string canvasJson, CancellationToken ct)
    {
        var canvas = ParseCanvas(canvasJson);

        var keys = canvas.Shapes
            .Where(s => s.Kind == "image" && !string.IsNullOrWhiteSpace(s.BlobKey))
            .Select(s => s.BlobKey!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var probes = new List<ImageProbe>(keys.Count);
        foreach (var key in keys)
        {
            var route = DescribeRoute(key);
            try
            {
                // Bypasses the cache deliberately — a probe that reports a
                // stale success tells you nothing about why the render is
                // failing now.
                _cache.Remove(key);
                var (_, bytes) = await LoadImageAsync(key, ct);
                probes.Add(new ImageProbe(key, route, true, null, bytes.Length));
            }
            catch (Exception ex)
            {
                probes.Add(new ImageProbe(
                    key, route, false, $"{ex.GetType().Name}: {ex.Message}", 0));
            }
        }
        return probes;
    }

    /// <summary>
    /// Decodes an image, or returns null and says which one failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A single unreadable image must never cost the whole poster.</b> The
    /// null check this replaces was written for exactly that, and it did not
    /// work: <c>SKBitmap.Decode</c> does not return null when it cannot make a
    /// codec for the bytes — it throws <see cref="ArgumentNullException"/> with
    /// the message "Value cannot be null. (Parameter 'codec')". The exception
    /// went straight past the guard, out of the render, and turned one
    /// unsupported logo into a campaign with no poster at all.
    /// </para>
    /// <para>
    /// Catching broadly is deliberate. Skia's decoders are native code reached
    /// through a thin wrapper, and the failure modes are not a documented set;
    /// the only safe assumption is that anything can come back out.
    /// </para>
    /// </remarks>
    private SKBitmap? TryDecode(byte[] bytes, string? key)
    {
        try
        {
            var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is not null) return bitmap;

            _logger.LogWarning(
                "PNG render: could not decode image {Key} ({Bytes} bytes) — "
                + "it will be missing from the poster", key, bytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "PNG render: decoding image {Key} ({Bytes} bytes) threw. The poster will "
                + "render without it rather than failing entirely.", key, bytes.Length);
        }

        return null;
    }

    /// <summary>
    /// Rasterises SVG bytes to PNG, leaving every other format untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neither output path can draw an SVG. The PNG path decodes bitmaps with
    /// <c>SKBitmap.Decode</c>, which handles raster formats only, and the PDF
    /// path hands bytes to QuestPDF's <c>Image()</c>, which is no different.
    /// The vector layer is not a way round this: it renders the canvas's own
    /// shapes, not an uploaded file.
    /// </para>
    /// <para>
    /// This runs once per key, before the cache is written, so both outputs see
    /// the same raster bytes and neither needs to know an SVG was ever
    /// involved. It matters that this is not left to the caller: a pharmacy
    /// uploading a logo as SVG is entirely ordinary — the brand-kit page offers
    /// it in the file picker — and every path that draws that logo would
    /// otherwise need the same special case.
    /// </para>
    /// </remarks>
    private (string Mime, byte[] Bytes) RasterizeIfVector(
        string key, (string Mime, byte[] Bytes) image)
    {
        if (!LooksLikeSvg(image.Mime, image.Bytes)) return image;

        try
        {
            if (SvgRasterizer.ToPng(image.Bytes) is not { } png)
            {
                _logger.LogWarning("SVG image {Key} did not parse or has no size; leaving it as-is", key);
                return image;
            }
            _logger.LogInformation("Rasterised SVG {Key} to PNG ({Bytes} bytes)", key, png.Length);
            return ("image/png", png);
        }
        catch (Exception ex)
        {
            // Returned unchanged rather than rethrown. TryDecode will drop it
            // and the rest of the poster still renders.
            _logger.LogError(ex, "Rasterising SVG image {Key} failed", key);
            return image;
        }
    }

    /// <summary>
    /// Whether these bytes are SVG, by content rather than by file name.
    /// </summary>
    /// <remarks>
    /// The declared type is not reliable enough to decide on alone: a blob key
    /// gets its type guessed from its extension, and a SAS URL often reports
    /// <c>application/octet-stream</c>. Sniffing the start of the payload
    /// catches both, and an XML prolog or a comment before the root element is
    /// common enough in exported SVGs to be worth allowing for.
    /// </remarks>
    private static bool LooksLikeSvg(string mime, byte[] bytes)
    {
        if (mime.Contains("svg", StringComparison.OrdinalIgnoreCase)) return true;
        return SvgRasterizer.LooksLikeSvg(bytes);
    }

    private async Task<(string Mime, byte[] Bytes)> LoadImageAsync(string blobKey, CancellationToken ct)
    {
        if (_cache.TryGetValue(blobKey, out (string Mime, byte[] Bytes) cached))
            return cached;

        (string Mime, byte[] Bytes) result;

        // Our own template-asset URLs (uploaded or AI-generated images) are
        // resolved straight from blob storage rather than fetched over HTTP.
        //
        // The alternative — letting the http branch below handle them — would
        // have the API make a request to itself. That is a wasted round trip at
        // best, and at worst it doesn't work at all: the URL stored in a canvas
        // is the one an administrator's browser used, which inside a container
        // network or behind a private ingress the API often cannot reach.
        var assetKey = TemplateAssetUrls.TryResolveBlobKey(blobKey);
        if (assetKey is not null && _blobs is not null)
        {
            using var stream = await _blobs.DownloadAsync(assetKey, ct);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            result = (GuessMimeFromKey(assetKey), ms.ToArray());
        }
        // http(s): only the few external hosts the original gallery templates
        // use, over https. Anything else a canvas names — another host, an
        // internal service, a metadata endpoint — is never requested (security
        // review SEC-01). Canvases are filtered before they get here; this is
        // the second line.
        else if (blobKey.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || blobKey.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsAllowedRemote(blobKey))
                throw new InvalidOperationException("Image source is not on the allow-list.");

            using var res = await _http.GetAsync(blobKey, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            if (res.Content.Headers.ContentLength is > MaxImageBytes)
                throw new InvalidOperationException("Image is too large.");
            var mime = res.Content.Headers.ContentType?.MediaType ?? GuessMimeFromKey(blobKey);
            var bytes = await ReadCappedAsync(await res.Content.ReadAsStreamAsync(ct), ct);
            result = (mime, bytes);
        }
        // data-URI passed straight through the canvas (e.g. small uploads).
        else if (blobKey.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = blobKey.IndexOf(',');
            if (comma <= 5) throw new InvalidOperationException("Malformed data URI.");
            var header = blobKey[5..comma]; // "image/jpeg;base64" or "image/png"
            var mime = header.Split(';')[0];
            var payload = blobKey[(comma + 1)..];
            var bytes = header.EndsWith("base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(payload)
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            result = (mime, bytes);
        }
        // A raw storage key: only the brand-logo area, which is the one kind
        // the render itself puts into a canvas (BrandCanvasAssets resolves
        // /api/brand-kit/logo to the poster owner's own logo key). Any other key
        // a canvas carried could be another pharmacy's file (SEC-02).
        else if (_blobs is not null && IsAllowedStorageKey(blobKey))
        {
            using var stream = await _blobs.DownloadAsync(blobKey, ct);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            result = (GuessMimeFromKey(blobKey), ms.ToArray());
        }
        else
        {
            throw new InvalidOperationException($"No provider available for image key '{blobKey}'.");
        }

        // Before the cache, so the conversion is paid for once per key and both
        // the PNG and the PDF path see the same raster bytes.
        result = RasterizeIfVector(blobKey, result);

        if (result.Bytes.Length > MaxImageBytes)
            throw new InvalidOperationException("Image is too large.");

        _cache.Set(blobKey, result, new MemoryCacheEntryOptions
        {
            SlidingExpiration = CacheEntry.SlidingExpiration,
            Size = Math.Max(1, result.Bytes.Length),
        });
        return result;
    }

    /// <summary>https on an allow-listed host and the default port — nothing else is fetched.</summary>
    public static bool IsAllowedRemote(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && Acme.Application.Posters.CanvasImageSources.AllowedHosts.Contains(uri.Host);

    /// <summary>Raw storage keys a render may read: the brand-logo area only, no traversal.</summary>
    public static bool IsAllowedStorageKey(string key) =>
        key.StartsWith("brand/logos/", StringComparison.Ordinal)
        && !key.Contains("..", StringComparison.Ordinal)
        && !key.Contains('\\');

    private static async Task<byte[]> ReadCappedAsync(Stream stream, CancellationToken ct)
    {
        await using (stream)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (ms.Length + read > MaxImageBytes)
                    throw new InvalidOperationException("Image is too large.");
                ms.Write(buffer, 0, read);
            }
            return ms.ToArray();
        }
    }

    private static string GuessMimeFromKey(string key)
    {
        // Strip query string before extension lookup.
        var q = key.IndexOf('?');
        var path = q > 0 ? key[..q] : key;
        var dot = path.LastIndexOf('.');
        if (dot < 0) return "image/png";
        return path[(dot + 1)..].ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "svg" => "image/svg+xml",
            _ => "image/png",
        };
    }

    /// <summary>
    /// Build an SVG containing every shape EXCEPT images. Images are drawn on
    /// top of this SVG by QuestPDF's native <c>Image()</c> at the correct
    /// canvas position — QuestPDF's SVG renderer can't reliably decode
    /// base64 image URIs.
    /// </summary>
    /// <summary>
    /// One slice of the render order: either a run of vector shapes rendered
    /// as SVG, or a single bitmap to paint as its own layer.
    /// </summary>
    private sealed record LayerBand(string? Svg, ShapeModel? Image);

    /// <summary>
    /// Walks the shapes in zIndex order and groups them into alternating
    /// vector / image bands, preserving the designer's stacking.
    ///
    /// The watermark is deliberately appended to the final vector band so it
    /// stays above everything, including a full-bleed photo — a watermark a
    /// photo can cover isn't a watermark.
    /// </summary>
    private static List<LayerBand> BuildLayerBands(
        CanvasModel canvas,
        IReadOnlyDictionary<string, FetchedImage> imageData,
        bool includeWatermark)
    {
        var bands = new List<LayerBand>();
        var pending = new List<ShapeModel>();

        void FlushVectors(bool withWatermark = false)
        {
            if (pending.Count == 0 && !withWatermark) return;
            bands.Add(new LayerBand(
                BuildSvg(canvas, withWatermark, imageData, pending, background: false), null));
            pending.Clear();
        }

        foreach (var shape in canvas.Shapes.OrderBy(s => s.ZIndex))
        {
            var isRenderableImage =
                shape.Kind == "image"
                && !string.IsNullOrWhiteSpace(shape.BlobKey)
                && imageData.ContainsKey(shape.BlobKey!);

            if (!isRenderableImage)
            {
                // Includes failed image fetches, which BuildSvg renders as a
                // visible placeholder box rather than a silent gap.
                pending.Add(shape);
                continue;
            }

            FlushVectors();
            bands.Add(new LayerBand(null, shape));
        }

        FlushVectors(withWatermark: includeWatermark);
        return bands;
    }

    /// <summary>Page-sized SVG carrying only the background colour.</summary>
    /// <summary>Canvas pixels are authored at this DPI.</summary>
    /// <remarks>
    /// Must stay in step with PRINT_DPI in packages/shared-types/src/page-size.ts,
    /// which is what turns a chosen paper size into canvas dimensions. If the two
    /// drift, an A3 design stops classifying as A3 here and quietly prints custom.
    /// </remarks>
    private const double PrintDpi = 150.0;

    private const double MmPerInch = 25.4;
    private const double PointsPerInch = 72.0;

    /// <summary>ISO 216 sizes, portrait, in millimetres.</summary>
    /// <remarks>
    /// Must stay in step with PAPER_MM in packages/shared-types/src/page-size.ts.
    /// A size the designer offers but this list omits prints at custom
    /// dimensions instead — close enough to look right on screen and wrong on a
    /// printer's guillotine.
    ///
    /// DL is not an ISO A size: it is a third of A4 landscape, which is exactly
    /// why a trifold A4 folds down into a DL envelope.
    /// </remarks>
    private static readonly (string Name, double WidthMm, double HeightMm)[] KnownPapers =
    {
        ("A4", 210, 297),
        ("A3", 297, 420),
        ("A5", 148, 210),
        ("DL", 99, 210),
    };

    /// <summary>
    /// Maps canvas pixel dimensions back to a paper size.
    /// </summary>
    /// <remarks>
    /// Aspect ratio alone is useless here: every ISO A size has the same 1:√2
    /// ratio, so A4 and A3 are indistinguishable by shape. Classification has to
    /// go on absolute size, which is why <see cref="PrintDpi"/> matters.
    ///
    /// Anything that isn't recognisably ISO — legacy 1920x1080 posters, or a
    /// hand-resized canvas — gets a page cut to its own dimensions rather than
    /// being forced onto A4, which previously stretched a 16:9 design to
    /// portrait A4 and distorted every shape on it.
    /// </remarks>
    private static PageSize ResolvePageSize(CanvasModel canvas)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0)
            return PageSizes.A4;

        string? bestName = null;
        var bestLandscape = false;
        var bestError = double.PositiveInfinity;

        foreach (var (name, widthMm, heightMm) in KnownPapers)
        {
            var portraitW = widthMm / MmPerInch * PrintDpi;
            var portraitH = heightMm / MmPerInch * PrintDpi;

            foreach (var landscape in new[] { false, true })
            {
                var pw = landscape ? portraitH : portraitW;
                var ph = landscape ? portraitW : portraitH;
                var error =
                    Math.Abs(pw - canvas.Width) / pw +
                    Math.Abs(ph - canvas.Height) / ph;

                if (error < bestError)
                {
                    bestError = error;
                    bestName = name;
                    bestLandscape = landscape;
                }
            }
        }

        // 10% combined error mirrors classifyPageSize()'s tolerance in
        // packages/shared-types/src/page-size.ts, so the badge the user sees in
        // the designer and the paper they actually get agree.
        if (bestName is null || bestError > 0.10)
            return CustomPageSize(canvas);

        // QuestPDF ships the ISO A sizes; DL is a trade size and has to be
        // built from millimetres. Constructed rather than approximated, because
        // a leaflet that is a millimetre out is a leaflet that is trimmed
        // crooked across a whole print run.
        var size = bestName switch
        {
            "A3" => PageSizes.A3,
            "A5" => PageSizes.A5,
            "DL" => MmPageSize(99, 210),
            _ => PageSizes.A4,
        };
        return bestLandscape ? size.Landscape() : size;
    }

    /// <summary>A paper size given in millimetres, for trade sizes QuestPDF has no constant for.</summary>
    private static PageSize MmPageSize(double widthMm, double heightMm) =>
        new(
            (float)(widthMm / MmPerInch * PointsPerInch),
            (float)(heightMm / MmPerInch * PointsPerInch));

    /// <summary>A page cut to the canvas's own dimensions, preserving aspect.</summary>
    private static PageSize CustomPageSize(CanvasModel canvas)
    {
        var width = (float)(canvas.Width / PrintDpi * PointsPerInch);
        var height = (float)(canvas.Height / PrintDpi * PointsPerInch);
        return new PageSize(width, height);
    }

    private static string EmptyCanvasSvg(CanvasModel canvas) =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {F(canvas.Width)} {F(canvas.Height)}\" "
        + "preserveAspectRatio=\"xMidYMid meet\">"
        + $"<rect width=\"100%\" height=\"100%\" fill=\"{WebUtility.HtmlEncode(canvas.Background)}\"/>"
        + "</svg>";

    private static string BuildSvg(
        CanvasModel canvas,
        bool includeWatermark,
        IReadOnlyDictionary<string, FetchedImage> imageData,
        IReadOnlyList<ShapeModel>? only = null,
        bool background = true)
    {
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ");
        sb.Append(F(canvas.Width));
        sb.Append(' ');
        sb.Append(F(canvas.Height));
        sb.Append("\" preserveAspectRatio=\"xMidYMid meet\">");

        // Band layers stack on top of the primary layer, which already carries
        // the background. Repainting it here would erase everything beneath.
        if (background)
        {
            sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"");
            sb.Append(WebUtility.HtmlEncode(canvas.Background));
            sb.Append("\"/>");
        }

        foreach (var shape in (only ?? canvas.Shapes).OrderBy(s => s.ZIndex))
        {
            // Image shapes: emit a placeholder box if the fetch failed so the
            // slot is still visible in the PDF. Otherwise the QuestPDF Image()
            // layer will paint over this space and the SVG's rendering is a no-op.
            if (shape.Kind == "image")
            {
                // An empty source is a deliberate absence, not a failure: the
                // booking QR of a pharmacy with no booking link, the logo slot
                // of one with no logo. `BrandCanvasAssets` empties them so that
                // every renderer draws nothing — and this one drew a grey box,
                // so a poster printed a blank square where the QR would be.
                if (string.IsNullOrWhiteSpace(shape.BlobKey))
                    continue;

                // A source that could not be fetched or decoded keeps its
                // placeholder, so a missing photo is visible rather than silent.
                if (!imageData.ContainsKey(shape.BlobKey!))
                    AppendImagePlaceholder(sb, shape);
                continue;
            }
            AppendShape(sb, shape);
        }

        if (includeWatermark)
            AppendWatermark(sb, canvas);

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void AppendImagePlaceholder(StringBuilder sb, ShapeModel shape)
    {
        var transform = $"translate({F(shape.Position.X)} {F(shape.Position.Y)}) "
                      + $"rotate({F(shape.Rotation)}) "
                      + $"scale({F(shape.Scale.X)} {F(shape.Scale.Y)})";
        sb.Append("<g transform=\"");
        sb.Append(transform);
        sb.Append("\" opacity=\"");
        sb.Append(F(shape.Opacity));
        sb.Append("\">");
        sb.Append("<rect width=\"");
        sb.Append(F(shape.Width));
        sb.Append("\" height=\"");
        sb.Append(F(shape.Height));
        sb.Append("\" fill=\"#e5e7eb\"/>");
        sb.Append("</g>");
    }

    private static void AppendShape(StringBuilder sb, ShapeModel shape)
    {
        var transform = $"translate({F(shape.Position.X)} {F(shape.Position.Y)}) "
                      + $"rotate({F(shape.Rotation)}) "
                      + $"scale({F(shape.Scale.X)} {F(shape.Scale.Y)})";
        sb.Append("<g transform=\"");
        sb.Append(transform);
        sb.Append("\" opacity=\"");
        sb.Append(F(shape.Opacity));
        sb.Append("\">");

        switch (shape.Kind)
        {
            case "rect":
                AppendRect(sb, shape);
                break;
            case "circle":
                AppendCircle(sb, shape);
                break;
            case "line":
                AppendLine(sb, shape);
                break;
            case "text":
                AppendText(sb, shape);
                break;
        }

        sb.Append("</g>");
    }

    private static void AppendRect(StringBuilder sb, ShapeModel shape)
    {
        sb.Append("<rect width=\"");
        sb.Append(F(shape.Width));
        sb.Append("\" height=\"");
        sb.Append(F(shape.Height));
        sb.Append("\" fill=\"");
        sb.Append(WebUtility.HtmlEncode(shape.Fill ?? "#000"));
        sb.Append('"');
        if (shape.CornerRadius is { } cr && cr > 0)
        {
            sb.Append(" rx=\"");
            sb.Append(F(cr));
            sb.Append("\" ry=\"");
            sb.Append(F(cr));
            sb.Append('"');
        }
        if (!string.IsNullOrEmpty(shape.Stroke) && (shape.StrokeWidth ?? 0) > 0)
        {
            sb.Append(" stroke=\"");
            sb.Append(WebUtility.HtmlEncode(shape.Stroke!));
            sb.Append("\" stroke-width=\"");
            sb.Append(F(shape.StrokeWidth!.Value));
            sb.Append('"');
        }
        sb.Append("/>");
    }

    private static void AppendCircle(StringBuilder sb, ShapeModel shape)
    {
        sb.Append("<circle r=\"");
        sb.Append(F(shape.Radius ?? 0));
        sb.Append("\" fill=\"");
        sb.Append(WebUtility.HtmlEncode(shape.Fill ?? "#000"));
        sb.Append('"');
        if (!string.IsNullOrEmpty(shape.Stroke) && (shape.StrokeWidth ?? 0) > 0)
        {
            sb.Append(" stroke=\"");
            sb.Append(WebUtility.HtmlEncode(shape.Stroke!));
            sb.Append("\" stroke-width=\"");
            sb.Append(F(shape.StrokeWidth!.Value));
            sb.Append('"');
        }
        sb.Append("/>");
    }

    private static void AppendLine(StringBuilder sb, ShapeModel shape)
    {
        if (shape.Points is not { Length: >= 4 }) return;

        var pts = new StringBuilder();
        for (var i = 0; i + 1 < shape.Points.Length; i += 2)
        {
            if (i > 0) pts.Append(' ');
            pts.Append(F(shape.Points[i]));
            pts.Append(',');
            pts.Append(F(shape.Points[i + 1]));
        }

        if (shape.Closed == true)
            sb.Append("<polygon points=\"");
        else
            sb.Append("<polyline points=\"");
        sb.Append(pts);
        sb.Append("\" fill=\"");
        sb.Append(shape.Closed == true ? WebUtility.HtmlEncode(shape.Stroke ?? "#000") : "none");
        sb.Append("\" stroke=\"");
        sb.Append(WebUtility.HtmlEncode(shape.Stroke ?? "#000"));
        sb.Append("\" stroke-width=\"");
        sb.Append(F(shape.StrokeWidth ?? 1));
        sb.Append("\" stroke-linecap=\"round\"/>");
    }

    private static void AppendText(StringBuilder sb, ShapeModel shape)
    {
        var fontSize = shape.FontSize ?? 16;
        // No HTML-encode: SafeFontStack contains single-quotes that must reach
        // the SVG parser verbatim to delimit multi-word family names.
        var family = SanitizeFont(shape.FontFamily);
        var color = WebUtility.HtmlEncode(shape.Fill ?? "#000");
        var anchor = shape.Align switch
        {
            "center" => "middle",
            "right" => "end",
            _ => "start",
        };
        var w = shape.Width;
        var x = anchor switch
        {
            "middle" => w / 2,
            "end" => w,
            _ => 0f,
        };

        var lineHeight = fontSize * 1.15f;
        var lines = WrapText(shape.Text ?? string.Empty, w, fontSize);

        for (var i = 0; i < lines.Count; i++)
        {
            sb.Append("<text x=\"");
            sb.Append(F(x));
            sb.Append("\" y=\"");
            sb.Append(F(fontSize + i * lineHeight));
            sb.Append("\" font-size=\"");
            sb.Append(F(fontSize));
            sb.Append("\" font-family=\"");
            sb.Append(family);
            sb.Append("\" fill=\"");
            sb.Append(color);
            sb.Append("\" text-anchor=\"");
            sb.Append(anchor);
            sb.Append("\">");
            sb.Append(WebUtility.HtmlEncode(lines[i]));
            sb.Append("</text>");
        }
    }

    private static IReadOnlyList<string> WrapText(string text, float width, float fontSize)
    {
        var hardLines = text.Split('\n');
        if (width <= 0) return hardLines;

        var approxCharWidth = fontSize * 0.55f;
        var maxChars = Math.Max(1, (int)Math.Floor(width / approxCharWidth));

        var output = new List<string>();
        foreach (var hard in hardLines)
        {
            if (hard.Length <= maxChars)
            {
                output.Add(hard);
                continue;
            }
            var words = hard.Split(' ');
            var line = "";
            foreach (var word in words)
            {
                var test = line.Length == 0 ? word : line + " " + word;
                if (test.Length > maxChars && line.Length > 0)
                {
                    output.Add(line);
                    line = word;
                }
                else
                {
                    line = test;
                }
            }
            if (line.Length > 0) output.Add(line);
        }
        return output;
    }

    private static void AppendWatermark(StringBuilder sb, CanvasModel canvas)
    {
        sb.Append("<text x=\"");
        sb.Append(F(canvas.Width - 16));
        sb.Append("\" y=\"");
        sb.Append(F(canvas.Height - 12));
        sb.Append("\" font-size=\"16\" font-family=\"");
        sb.Append(SafeFontStack);
        sb.Append("\" fill=\"rgba(15,23,42,0.55)\" text-anchor=\"end\">");
        sb.Append(BrandWatermark);
        sb.Append("</text>");
    }

    /// <summary>
    /// SkiaSharp (via QuestPDF) renders SVG text using OS-installed fonts. In
    /// containers we can't rely on Inter / Helvetica being present, and Skia's
    /// missing-glyph behaviour is to render nothing rather than fall back — so
    /// posters ship as "shapes with no text".
    ///
    /// We install Liberation Sans + DejaVu Sans in the Docker image and force
    /// every text shape to use those, regardless of what the canvas requested.
    /// The comma-separated list lets Skia try each until it finds one it has.
    /// </summary>
    // SVG/CSS rule: multi-word family names MUST be single-quoted, otherwise the
    // parser splits them into separate names ("Liberation" + "Sans"), none of
    // which exist, and every <text> renders blank. That's what caused
    // "shapes-and-colors-but-no-text" posters in production.
    private const string SafeFontStack = "'Liberation Sans', 'DejaVu Sans', Arial, Helvetica, sans-serif";

    private static string SanitizeFont(string? family) => SafeFontStack;

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static CanvasModel ParseCanvas(string json) =>
        JsonSerializer.Deserialize<CanvasModel>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? new CanvasModel();

    private sealed record Vector2Model
    {
        public float X { get; init; }
        public float Y { get; init; }
    }

    private sealed record ShapeModel
    {
        public string Id { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public Vector2Model Position { get; init; } = new();
        public float Rotation { get; init; }
        public Vector2Model Scale { get; init; } = new() { X = 1, Y = 1 };
        public float Opacity { get; init; } = 1;
        public int ZIndex { get; init; }
        public float Width { get; init; }
        public float Height { get; init; }
        public float? Radius { get; init; }
        public float? FontSize { get; init; }
        public string? FontFamily { get; init; }
        public string? Text { get; init; }
        public string? Fill { get; init; }
        public string? Stroke { get; init; }
        public float? StrokeWidth { get; init; }
        public float? CornerRadius { get; init; }
        public string? Align { get; init; }
        public float[]? Points { get; init; }
        public bool? Closed { get; init; }
        /// <summary>
        /// For image shapes: an http(s) URL, a data-URI, or an Azure Blob key.
        /// The canvas schema uses "blobKey" verbatim.
        /// </summary>
        public string? BlobKey { get; init; }
    }

    private sealed record CanvasModel
    {
        public string Background { get; init; } = "#ffffff";
        public float Width { get; init; } = 1240;
        public float Height { get; init; } = 1754;
        public List<ShapeModel> Shapes { get; init; } = new();
    }
}
