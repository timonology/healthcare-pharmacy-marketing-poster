namespace Acme.Application.Videos;

/// <summary>
/// What a pharmacist may attach to a video, and why each limit is there.
/// </summary>
/// <remarks>
/// <para>
/// Pure and public so the rules can be tested head-on, the same choice
/// <c>CampaignService.IsReachableFromTheInternet</c> and
/// <c>SmokeAccountPolicy</c> make. A limit enforced only inside a controller
/// action is a limit that needs a host to exercise, and one that needs a host
/// tends not to get exercised.
/// </para>
/// <para>
/// <b>None of these are about cost.</b> Attaching photographs is free — the
/// pictures are the pharmacy's own and no model is called for them. The limits
/// are about what makes a watchable fifteen-second video and what a render can
/// carry without falling over.
/// </para>
/// </remarks>
public static class VideoImagePolicy
{
    /// <summary>
    /// The most pictures one video may carry.
    /// </summary>
    /// <remarks>
    /// Four, because a 15-second clip is six to eight beats and pinning more
    /// than half of them leaves no room for the stock footage that carries the
    /// pace. Kept in step with <c>MAX_VIDEO_IMAGES</c> in shared-types.
    /// </remarks>
    public const int MaxImages = 4;

    /// <summary>Eight megabytes, per picture.</summary>
    /// <remarks>
    /// Generous for a phone photograph and small enough that four of them do not
    /// turn a render into an upload. The cap is per file rather than per
    /// request so the message can name the file that was too big.
    /// </remarks>
    public const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// What ffmpeg will reliably turn into a clip.
    /// </summary>
    /// <remarks>
    /// <b>No SVG here, unlike the brand-kit logo.</b> A logo is drawn onto a
    /// canvas by a renderer that understands vectors; this picture is handed to
    /// ffmpeg, which rasterises nothing. An SVG accepted here would be a file
    /// that uploads cleanly and then silently fails to become a beat.
    /// </remarks>
    public static readonly IReadOnlySet<string> AllowedContentTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg",
            "image/png",
            "image/webp",
        };

    /// <summary>Null when this file may be attached, or the reason it may not.</summary>
    public static string? WhyFileRefused(string? contentType, long lengthBytes)
    {
        if (lengthBytes <= 0)
            return "That file is empty.";

        if (lengthBytes > MaxBytes)
            return $"Images must be under {MaxBytes / (1024 * 1024)}MB. That one is "
                 + $"{Megabytes(lengthBytes)}MB.";

        if (string.IsNullOrWhiteSpace(contentType) || !AllowedContentTypes.Contains(contentType))
            return "Images must be JPEG, PNG or WebP.";

        return null;
    }

    /// <summary>Where this owner's attached uploads live.</summary>
    public static string AttachmentPrefixFor(string ownerId) =>
        $"videos/{ownerId}/attachments/";

    /// <summary>
    /// True when this key is an upload <i>this owner</i> made for a video.
    /// </summary>
    /// <remarks>
    /// Used by delete. A logo or a published poster reused as B-roll is owned
    /// by another feature and is still on screen elsewhere, so deleting the
    /// video that borrowed it must not delete it — only the pictures uploaded
    /// for that video are the video's to remove.
    /// </remarks>
    public static bool IsOwnAttachment(string? blobKey, string ownerId) =>
        !string.IsNullOrWhiteSpace(ownerId)
        && !string.IsNullOrWhiteSpace(blobKey)
        && blobKey.StartsWith(AttachmentPrefixFor(ownerId), StringComparison.Ordinal);

    /// <summary>
    /// Null when this owner may attach this blob key, or the reason they may not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The blob key arrives in the request body, so it is attacker-chosen.</b>
    /// Without this check a signed-in pharmacy could name any key in the
    /// account — another pharmacy's poster, say — and get two things it should
    /// never have: the picture rendered into a video it can download, and, on
    /// deleting that video, the other pharmacy's blob deleted with it. The
    /// upload endpoint only ever mints keys under the owner's own prefix, so
    /// nothing legitimate is refused here.
    /// </para>
    /// <para>
    /// Three shapes are allowed, one per source, each pinned to this owner:
    /// their video uploads, their published posters, their brand logo. The
    /// prefixes are duplicated from the services that mint them
    /// (<c>PosterImageService</c>, <c>AzureBrandAssetService</c>) — a copy, and
    /// therefore something that can drift, which is why
    /// <c>VideoImagePolicyTests</c> builds keys the same way those services do
    /// rather than pasting literals.
    /// </para>
    /// <para>
    /// <c>..</c> is refused outright. Azure blob names are flat, so a relative
    /// segment is part of the name rather than a path to resolve and the prefix
    /// check already holds — but the render worker joins this key onto a local
    /// path, and a rule that holds for one reason at one end is not worth
    /// relying on at the other.
    /// </para>
    /// </remarks>
    public static string? WhyKeyRefused(string? blobKey, string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return "That image could not be attached.";

        var key = blobKey?.Trim();
        if (string.IsNullOrWhiteSpace(key))
            return "That image could not be attached.";

        if (key.Contains("..", StringComparison.Ordinal) || key.StartsWith('/'))
            return "That image could not be attached.";

        var allowed =
            key.StartsWith(AttachmentPrefixFor(ownerId), StringComparison.Ordinal)
            || key.StartsWith($"posters/{ownerId}/", StringComparison.Ordinal)
            || key.StartsWith($"brand/logos/{ownerId}/", StringComparison.Ordinal);

        // Deliberately the same sentence as the cases above. The caller is
        // either the product's own dialog, which cannot reach here, or somebody
        // naming keys by hand — and telling the second which part of the key
        // was wrong is help they should not get.
        return allowed ? null : "That image could not be attached.";
    }

    /// <summary>A file's size in megabytes, rounded <i>up</i> to one decimal.</summary>
    /// <remarks>
    /// Up rather than to-nearest, because this number is only ever printed
    /// beside the cap it exceeded. Rounding to nearest turned the commonest
    /// refusal — a phone photograph a little over the line — into "Images must
    /// be under 8MB. That one is 8MB.", which reads like a bug in the site and
    /// gives the pharmacist nothing to act on. Rounding up means the figure is
    /// always visibly larger than the limit.
    /// </remarks>
    private static double Megabytes(long bytes) =>
        Math.Ceiling(bytes / 1024d / 1024d * 10) / 10;

    /// <summary>Null when this many images is allowed, or the reason it is not.</summary>
    /// <remarks>
    /// Zero is fine: attaching pictures is optional, and a video with none is
    /// the ordinary case.
    /// </remarks>
    public static string? WhyCountRefused(int count)
    {
        if (count < 0) return "That is not a number of images.";
        if (count > MaxImages)
            return $"You can attach up to {MaxImages} images. That is {count}.";
        return null;
    }
}
