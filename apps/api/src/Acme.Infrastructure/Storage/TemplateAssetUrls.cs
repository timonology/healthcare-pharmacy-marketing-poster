namespace Acme.Infrastructure.Storage;

/// <summary>
/// The mapping between a template-asset URL and its blob key.
/// </summary>
/// <remarks>
/// Static and dependency-free because two very different callers need it and
/// must agree exactly: <see cref="TemplateAssetService"/> when minting and
/// serving assets, and the PDF exporter when deciding whether it can read a
/// canvas image straight from blob storage instead of over HTTP. If those two
/// ever disagreed, exports would silently start fetching the API's own URLs —
/// which usually still works, so the mistake would not surface until it hit a
/// network where it doesn't.
/// </remarks>
public static class TemplateAssetUrls
{
    /// <summary>Blob key prefix.</summary>
    public const string BlobPrefix = "templates/assets";

    /// <summary>Route prefix served by AssetsController.</summary>
    public const string UrlPrefix = "/api/assets/templates/";

    public static readonly IReadOnlyDictionary<string, string> ExtensionByContentType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"] = "png",
            ["image/jpeg"] = "jpg",
            ["image/webp"] = "webp",
        };

    private static readonly HashSet<string> KnownExtensions =
        new(ExtensionByContentType.Values, StringComparer.OrdinalIgnoreCase);

    public static string UrlFor(string fileName) => UrlPrefix + fileName;

    /// <summary>
    /// Blob key for an asset URL, or null when the value isn't one of ours.
    /// Accepts both the relative form we store and an absolute URL, since a
    /// canvas may have been authored against a different host.
    /// </summary>
    public static string? TryResolveBlobKey(string? urlOrPath)
    {
        if (string.IsNullOrWhiteSpace(urlOrPath)) return null;

        string path;

        // The relative form is tested FIRST, before any URI parsing.
        //
        // On Unix, Uri.TryCreate("/api/assets/…", UriKind.Absolute, out _)
        // returns true with Scheme == "file" — a leading slash is a valid
        // absolute file URI there. Parsing first therefore sent our own stored
        // URLs into the non-http guard below, which returned null, and every
        // template image fell through to be treated as a literal blob key.
        // The result was a BlobNotFound on a path that was never a blob key,
        // and a poster silently rendered without its photograph.
        //
        // Windows does not behave this way, so the bug is platform-specific —
        // exactly the kind that survives a review and only shows in the logs.
        if (urlOrPath.StartsWith('/'))
        {
            path = urlOrPath;
        }
        else if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var absolute))
        {
            // Reject non-http schemes outright — a "data:" URI can contain
            // anything and must not be pattern-matched into a blob key.
            if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps)
                return null;
            path = absolute.AbsolutePath;
        }
        else
        {
            return null;
        }

        if (!path.StartsWith(UrlPrefix, StringComparison.OrdinalIgnoreCase)) return null;

        var fileName = path[UrlPrefix.Length..];
        return IsSafeFileName(fileName) ? $"{BlobPrefix}/{fileName}" : null;
    }

    /// <summary>
    /// Names we mint are a 32-character hex GUID plus a known extension.
    /// Anything else is rejected rather than sanitised: no legitimate caller
    /// sends a different shape, so this closes path traversal without having to
    /// reason about encodings, "..", or Unicode normalisation.
    /// </summary>
    public static bool IsSafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        var dot = fileName.LastIndexOf('.');
        if (dot != 32 || dot == fileName.Length - 1) return false;

        var stem = fileName[..dot];
        var ext = fileName[(dot + 1)..];

        return stem.All(Uri.IsHexDigit) && KnownExtensions.Contains(ext);
    }

    public static string ContentTypeFor(string fileName)
    {
        var ext = fileName[(fileName.LastIndexOf('.') + 1)..].ToLowerInvariant();
        return ext switch
        {
            "png" => "image/png",
            "webp" => "image/webp",
            _ => "image/jpeg",
        };
    }
}
