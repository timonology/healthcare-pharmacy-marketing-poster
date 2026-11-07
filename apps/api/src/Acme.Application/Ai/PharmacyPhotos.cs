using System.Globalization;
using System.Text.RegularExpressions;

namespace Acme.Application.Ai;

/// <summary>
/// Where a pharmacy's own photo uploads for "Design with AI" are kept
/// (security review SEC-15).
/// </summary>
/// <remarks>
/// <para>
/// They used to go into the template-asset area, which serves every file to
/// anyone by name. A pharmacist's photo of their team or their counter is not
/// a gallery image, so uploads now live in a private area under the owner's own
/// prefix. They are served only to that pharmacy (through an authenticated
/// route that looks in the caller's own prefix, so another pharmacy's file
/// cannot be named), and read from the same place when the design runs.
/// </para>
/// <para>
/// An upload is only needed while the pharmacist is designing — the finished
/// poster carries its own baked copy — so uploads older than
/// <see cref="KeptFor"/> are deleted. The date is in the file name, so no
/// database row is needed to know an upload's age.
/// </para>
/// </remarks>
public interface IPharmacyPhotoStore
{
    Task SaveAsync(string ownerId, string fileName, Stream content, string contentType, CancellationToken ct);

    /// <summary>The upload, or null when it does not exist in this owner's area.</summary>
    Task<Stream?> OpenAsync(string ownerId, string fileName, CancellationToken ct);

    /// <summary>Deletes this owner's uploads dated before the cut-off; returns how many.</summary>
    Task<int> DeleteOlderThanAsync(string ownerId, DateTime cutoffUtc, CancellationToken ct);
}

public static class PharmacyPhotos
{
    /// <summary>The authenticated route an upload is served from.</summary>
    public const string UrlPrefix = "/api/posters/photos/";

    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    private static readonly Regex FileNamePattern = new(
        @"^(?<date>\d{8})-[0-9a-f]{32}\.(?<ext>jpg|png|webp)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string NewFileName(DateTime utcNow, string contentType) =>
        $"{utcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.{Extension(contentType)}";

    public static string UrlFor(string fileName) => UrlPrefix + fileName;

    public static bool IsValidFileName(string? fileName) =>
        fileName is not null && FileNamePattern.IsMatch(fileName);

    /// <summary>The file name from an upload URL (relative, or absolute on any host), or false.</summary>
    public static bool TryFileName(string? urlOrPath, out string fileName)
    {
        fileName = "";
        if (string.IsNullOrWhiteSpace(urlOrPath)) return false;

        var path = urlOrPath.Trim();
        if (!path.StartsWith('/'))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return false;
            path = uri.AbsolutePath;
        }

        if (!path.StartsWith(UrlPrefix, StringComparison.Ordinal)) return false;
        var candidate = path[UrlPrefix.Length..];
        if (!IsValidFileName(candidate)) return false;

        fileName = candidate;
        return true;
    }

    /// <summary>The day an upload was made, from its name.</summary>
    public static DateTime? DateOf(string fileName)
    {
        var m = FileNamePattern.Match(fileName);
        return m.Success
               && DateTime.TryParseExact(m.Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture,
                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;
    }

    public static string ContentTypeFor(string fileName) =>
        fileName.EndsWith(".png", StringComparison.Ordinal) ? "image/png"
        : fileName.EndsWith(".webp", StringComparison.Ordinal) ? "image/webp"
        : "image/jpeg";

    private static string Extension(string contentType) => contentType switch
    {
        "image/png" => "png",
        "image/webp" => "webp",
        _ => "jpg",
    };
}
