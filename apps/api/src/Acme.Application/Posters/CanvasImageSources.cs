using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Acme.Application.Posters;

/// <summary>
/// Which image sources a poster may draw — the rule that keeps the renderer
/// from fetching what a user typed (security review SEC-01, SEC-02).
/// </summary>
/// <remarks>
/// <para>
/// A canvas is a document the pharmacy writes, and every renderer (PDF, PNG,
/// email image, display screens) loads its images server-side. Before this, an
/// image whose source was <c>http://anything</c> was fetched by the API — any
/// host, internal ones included — and any other string was read straight from
/// blob storage, whoever's file it was. Both are now refused:
/// </para>
/// <list type="bullet">
///   <item>our template assets (<c>/api/assets/templates/{guid}.ext</c>, relative
///   or absolute) — read from storage, never fetched;</item>
///   <item>the brand routes (<c>/api/brand-kit/logo</c>, <c>/booking-qr</c>), which
///   the render resolves to <i>this</i> pharmacy's own logo and code;</item>
///   <item><c>data:image/…</c> — the editor stores uploaded pictures inline;</item>
///   <item>https on <see cref="AllowedHosts"/> only — the stock photos a few of
///   the original gallery templates still use.</item>
/// </list>
/// <para>
/// Everything else — another host, a private address, a raw storage key — is
/// refused when a poster is saved, and blanked at render as a second line of
/// defence for anything stored before the rule existed.
/// </para>
/// </remarks>
public static class CanvasImageSources
{
    /// <summary>External hosts an image may come from. Keep it short.</summary>
    public static readonly IReadOnlySet<string> AllowedHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "images.unsplash.com" };

    private const string TemplateAssetPrefix = "/api/assets/templates/";
    private const string BrandLogoPath = "/api/brand-kit/logo";
    private const string BookingQrPath = "/api/brand-kit/booking-qr";

    private static readonly Regex AssetName = new(
        "^[0-9a-fA-F]{32}\\.(png|jpg|jpeg|webp)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DataImage = new(
        "^data:image/(png|jpeg|jpg|webp|gif);base64,", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Whether a poster may draw an image from this source.</summary>
    public static bool IsAllowed(string? src)
    {
        if (string.IsNullOrWhiteSpace(src)) return true;
        var value = src.Trim();

        if (DataImage.IsMatch(value)) return true;
        if (value.StartsWith('/')) return IsOwnPath(value);

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false; // a raw storage key
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;

        // Our own paths on whatever host the canvas was authored against: the
        // renderer resolves these from storage and never makes the request.
        if (IsOwnPath(uri.AbsolutePath)) return true;

        return uri.Scheme == Uri.UriSchemeHttps
               && uri.IsDefaultPort
               && AllowedHosts.Contains(uri.Host);
    }

    private static bool IsOwnPath(string path)
    {
        if (path.Equals(BrandLogoPath, StringComparison.OrdinalIgnoreCase)
            || path.Equals(BookingQrPath, StringComparison.OrdinalIgnoreCase))
            return true;

        return path.StartsWith(TemplateAssetPrefix, StringComparison.OrdinalIgnoreCase)
               && AssetName.IsMatch(path[TemplateAssetPrefix.Length..]);
    }

    /// <summary>Why a canvas cannot be saved, or null when every image is allowed.</summary>
    /// <remarks>
    /// The canvas is parsed, not pattern-matched (security review R2-05). The
    /// renderer reads property names case-insensitively and unescaped, so a
    /// check on the literal text <c>"blobKey"</c> missed <c>"BlobKey"</c> —
    /// which the renderer still drew. Every property the renderer would take
    /// as a source is judged here, whatever its case or escaping, including a
    /// duplicate that would override an allowed one.
    /// </remarks>
    public static string? WhyRefused(string? canvasJson)
    {
        if (string.IsNullOrEmpty(canvasJson)) return null;

        List<string?> sources;
        try { sources = Sources(JsonNode.Parse(canvasJson)).ToList(); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // ArgumentException: a property named twice in one object. The
            // renderer would take the last one, so it is refused rather than guessed.
            return "The poster could not be read.";
        }

        foreach (var src in sources)
        {
            if (!IsAllowed(src))
                return "This poster uses an image from a source we can't print. Upload the picture "
                       + "instead of linking to it.";
        }
        return null;
    }

    /// <summary>
    /// The canvas with every disallowed image source blanked. Renderers skip an
    /// empty source, so the poster prints with a gap rather than fetching it.
    /// </summary>
    /// <remarks>
    /// Returned unchanged when nothing needs blanking, so the common case does
    /// not pay for a re-serialisation.
    /// </remarks>
    public static string StripDisallowed(string canvasJson)
    {
        if (string.IsNullOrEmpty(canvasJson)) return canvasJson;

        try
        {
            var root = JsonNode.Parse(canvasJson);
            var changed = false;
            foreach (var (owner, name) in SourceProperties(root))
            {
                if (owner[name] is JsonValue v && v.TryGetValue(out string? src) && IsAllowed(src))
                    continue;
                owner[name] = "";
                changed = true;
            }
            return changed ? root!.ToJsonString() : canvasJson;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // Unreadable, or a property named twice — neither can be saved
            // (WhyRefused refuses both), so this is a document from outside the
            // save path. Draw it with no pictures rather than guess.
            return EmptyImagesFallback;
        }
    }

    private static IEnumerable<string?> Sources(JsonNode? root)
    {
        foreach (var (owner, name) in SourceProperties(root))
        {
            if (owner[name] is JsonValue v && v.TryGetValue(out string? src))
                yield return src;
            else if (owner[name] is not null)
                yield return NotText; // an object or number where a source belongs
        }
    }

    /// <summary>Every (object, property name) pair whose name is <c>blobKey</c> in any case.</summary>
    private static List<(JsonObject Owner, string Name)> SourceProperties(JsonNode? root)
    {
        var found = new List<(JsonObject, string)>();
        var stack = new Stack<JsonNode?>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case JsonObject obj:
                    foreach (var (name, child) in obj)
                    {
                        if (string.Equals(name, SourceProperty, StringComparison.OrdinalIgnoreCase))
                            found.Add((obj, name));
                        else
                            stack.Push(child);
                    }
                    break;
                case JsonArray arr:
                    foreach (var child in arr) stack.Push(child);
                    break;
            }
        }
        return found;
    }

    private const string SourceProperty = "blobKey";

    /// <summary>An empty canvas, drawn when a stored one cannot be read safely.</summary>
    private const string EmptyImagesFallback = "{\"shapes\":[]}";

    /// <summary>Stands in for a source that is not a string; never allowed.</summary>
    private const string NotText = "(not text)";
}
