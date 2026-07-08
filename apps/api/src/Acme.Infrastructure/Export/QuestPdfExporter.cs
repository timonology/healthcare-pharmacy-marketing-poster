using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Acme.Application.Export;
using Acme.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Acme.Infrastructure.Export;

public sealed class QuestPdfExporter : IPosterPdfExporter
{
    private const string BrandName = "Sonar Marketing25";
    private const string BrandWatermark = "Made with Sonar Marketing25 · sonarinformatics.com";

    /// <summary>Cache image bytes so a poster sent to 200 recipients only downloads each image once.</summary>
    private static readonly ConcurrentDictionary<string, (string Mime, byte[] Bytes)> _cache = new();

    /// <summary>Reused HttpClient — thread-safe and cheap to keep alive.</summary>
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
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
        var svg = BuildSvg(canvas, request.IncludeWatermark, imageData);

        var pageSize = PageSizes.A4;
        var xScale = pageSize.Width / canvas.Width;   // points per canvas unit (x)
        var yScale = pageSize.Height / canvas.Height; // points per canvas unit (y)

        // Order image shapes by zIndex so lower z renders first.
        var imageShapes = canvas.Shapes
            .Where(s => s.Kind == "image" && !string.IsNullOrWhiteSpace(s.BlobKey))
            .OrderBy(s => s.ZIndex)
            .ToList();

        var pdf = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(pageSize);
                page.Margin(0);
                page.PageColor(canvas.Background);

                page.Content().Layers(layers =>
                {
                    // Primary layer defines the page size and paints vectors + text.
                    layers.PrimaryLayer().Svg(svg);

                    // Overlay each fetched bitmap at its canvas position, scaled
                    // to A4 points.
                    foreach (var shape in imageShapes)
                    {
                        if (!imageData.TryGetValue(shape.BlobKey!, out var img))
                            continue;

                        var x = shape.Position.X * xScale;
                        var y = shape.Position.Y * yScale;
                        var w = Math.Max(1, shape.Width * xScale);
                        var h = Math.Max(1, shape.Height * yScale);

                        layers.Layer()
                            .AlignLeft()
                            .AlignTop()
                            .TranslateX(x)
                            .TranslateY(y)
                            .Width(w)
                            .Height(h)
                            .Image(img.Bytes)
                            .FitUnproportionally();
                    }
                });
            });
        }).GeneratePdf();

        return pdf;
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
                    _logger.LogWarning(ex, "Skipping poster image '{Key}' — could not fetch.", key);
                }
            });
        return results;
    }

    private readonly record struct FetchedImage(string Mime, byte[] Bytes);

    private async Task<(string Mime, byte[] Bytes)> LoadImageAsync(string blobKey, CancellationToken ct)
    {
        if (_cache.TryGetValue(blobKey, out var cached))
            return cached;

        (string Mime, byte[] Bytes) result;

        // http(s) URLs (Unsplash, seeded template assets, brand kit SAS urls).
        if (blobKey.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || blobKey.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var res = await _http.GetAsync(blobKey, ct);
            res.EnsureSuccessStatusCode();
            var mime = res.Content.Headers.ContentType?.MediaType ?? GuessMimeFromKey(blobKey);
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
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
        // Azure Blob storage key (user uploads).
        else if (_blobs is not null)
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

        _cache.TryAdd(blobKey, result);
        return result;
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
    private static string BuildSvg(
        CanvasModel canvas,
        bool includeWatermark,
        IReadOnlyDictionary<string, FetchedImage> imageData)
    {
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ");
        sb.Append(F(canvas.Width));
        sb.Append(' ');
        sb.Append(F(canvas.Height));
        sb.Append("\" preserveAspectRatio=\"xMidYMid meet\">");

        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"");
        sb.Append(WebUtility.HtmlEncode(canvas.Background));
        sb.Append("\"/>");

        foreach (var shape in canvas.Shapes.OrderBy(s => s.ZIndex))
        {
            // Image shapes: emit a placeholder box if the fetch failed so the
            // slot is still visible in the PDF. Otherwise the QuestPDF Image()
            // layer will paint over this space and the SVG's rendering is a no-op.
            if (shape.Kind == "image")
            {
                var missing = string.IsNullOrWhiteSpace(shape.BlobKey)
                    || !imageData.ContainsKey(shape.BlobKey!);
                if (missing)
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
