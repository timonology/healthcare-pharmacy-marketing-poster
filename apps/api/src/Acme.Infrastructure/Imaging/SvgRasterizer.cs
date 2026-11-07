using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;
using Svg.Skia;

namespace Acme.Infrastructure.Imaging;

/// <summary>
/// SVG to PNG. Used when a logo is uploaded (security review SEC-10) and by the
/// renderer for any SVG stored before that.
/// </summary>
/// <remarks>
/// <para>
/// An SVG is a document that can carry script. Stored as uploaded and served
/// from our own origin, it is a stored cross-site-scripting vector against
/// anyone who opens its URL. Rasterising at upload keeps what a pharmacy
/// wants — "upload my SVG logo" still works — and stores only pixels.
/// </para>
/// <para>
/// Svg.Skia does not run script, but an SVG can still point outside itself:
/// <c>&lt;image href="http://…"&gt;</c>, a CSS <c>url(…)</c> or <c>@import</c>,
/// a DTD entity, or a second SVG nested as a <c>data:</c> URI that carries any
/// of those. Parsed on the server, any of those is a request from inside
/// our network (the SSRF of SEC-01 by another door) or a local file read.
/// <see cref="IsSelfContained"/> refuses all of them before the parser sees the
/// file; only in-document (<c>#id</c>) and <c>data:</c> references pass.
/// </para>
/// </remarks>
public static class SvgRasterizer
{
    /// <summary>Longest edge, in pixels, an SVG is drawn to.</summary>
    /// <remarks>
    /// The canvas is 1240px wide, so a logo across a third of it wants about
    /// 400px. 1024 leaves room to place one large without it going soft.
    /// </remarks>
    public const float LongestEdgePx = 1024f;

    // In-document references (#id) and inline raster pictures are the only
    // references allowed. Inline SVG is not: a nested document is never seen
    // by this check, and could point outside itself (security review R2-05).
    private const string AllowedReference = "(?!#|data:image/(?:png|jpe?g|webp|gif)[;,])";

    private static readonly Regex ExternalHref = new(
        "href\\s*=\\s*[\"']\\s*" + AllowedReference,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex ExternalCssUrl = new(
        "url\\(\\s*[\"']?\\s*" + AllowedReference,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // CSS escapes ("u\\72l(", "@\\69mport") spell the same words in a form the
    // patterns above cannot see. A logo has no use for them.
    private static readonly Regex CssEscape = new(
        "\\\\[0-9a-fA-F]{1,6}",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    static SvgRasterizer() => DisableExternalLoading();

    /// <summary>
    /// Turns off the SVG library's own fetching of external images, elements
    /// and DTDs, where its version exposes the switches.
    /// </summary>
    /// <remarks>
    /// Belt and braces under <see cref="IsSelfContained"/>: the pattern check
    /// is the guarantee, this is so a gap in it is not a fetch. Done by
    /// reflection because the switches' names and types differ between
    /// releases of the library, and a missing one must not stop the API
    /// starting. Static settings, so this applies to every parse.
    /// </remarks>
    private static void DisableExternalLoading()
    {
        var type = Type.GetType("Svg.SvgDocument, Svg.Custom", throwOnError: false)
                   ?? Type.GetType("Svg.SvgDocument, Svg", throwOnError: false);
        if (type is null) return;

        foreach (var name in new[] { "ResolveExternalImages", "ResolveExternalElements", "ResolveExternalXmlEntites", "ResolveExternalXmlEntities" })
        {
            var prop = type.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (prop is null || !prop.CanWrite) continue;
            try
            {
                if (prop.PropertyType.IsEnum) prop.SetValue(null, Enum.ToObject(prop.PropertyType, 0));
                else if (prop.PropertyType == typeof(bool)) prop.SetValue(null, false);
            }
            catch (Exception) { /* best effort; IsSelfContained still applies */ }
        }

        var dtd = type.GetProperty("DisableDtdProcessing", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (dtd is { CanWrite: true } && dtd.PropertyType == typeof(bool))
        {
            try { dtd.SetValue(null, true); } catch (Exception) { /* best effort */ }
        }
    }

    // A plain DOCTYPE line (no internal subset) — Illustrator and Inkscape
    // write one. Removed before parsing so the parser never resolves its DTD.
    private static readonly Regex PlainDoctype = new(
        "<!DOCTYPE[^>\\[]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The SVG text with a plain DOCTYPE line removed.</summary>
    public static string WithoutDoctype(string svgText) => PlainDoctype.Replace(svgText, "");

    /// <summary>
    /// Whether the SVG (after <see cref="WithoutDoctype"/>) refers only to
    /// itself: no DTD, no entity, no import, and every <c>href</c> and CSS
    /// <c>url()</c> either <c>#local</c> or <c>data:</c>.
    /// </summary>
    public static bool IsSelfContained(string svgText) =>
        !svgText.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
        && !svgText.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase)
        && !svgText.Contains("@import", StringComparison.OrdinalIgnoreCase)
        && !ExternalHref.IsMatch(svgText)
        && !ExternalCssUrl.IsMatch(svgText)
        && !CssEscape.IsMatch(svgText);

    /// <summary>Whether these bytes are SVG, by content, allowing for an XML prolog or comments.</summary>
    public static bool LooksLikeSvg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return false;
        var head = Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, 1024)]);
        return head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The SVG drawn to a transparent PNG, or null when it refers outside
    /// itself, does not parse, or has no size.
    /// </summary>
    public static byte[]? ToPng(byte[] svgBytes)
    {
        var text = WithoutDoctype(Encoding.UTF8.GetString(svgBytes));
        if (!IsSelfContained(text)) return null;

        using var svg = new SKSvg();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
        svg.Load(input);
        if (svg.Picture is not { } picture) return null;

        var bounds = picture.CullRect;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        // Scaled up, never down: an SVG authored at 24px would otherwise
        // rasterise to 24px and be placed across a third of an A4 poster.
        var scale = Math.Clamp(LongestEdgePx / Math.Max(bounds.Width, bounds.Height), 1f, 16f);
        // And never enormous: a 100000px-wide viewBox would be a memory bomb.
        scale = Math.Min(scale, 4096f / Math.Max(bounds.Width, bounds.Height));
        var width = Math.Clamp((int)Math.Ceiling(bounds.Width * scale), 1, 4096);
        var height = Math.Clamp((int)Math.Ceiling(bounds.Height * scale), 1, 4096);

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        // Transparent, not white: a logo sits over the design.
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
        canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
