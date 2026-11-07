using Acme.Application.Videos;
using Acme.Domain.Videos;

namespace Acme.Application.Displays;

/// <summary>
/// The rules for putting a video on an in-pharmacy screen, in one place.
/// </summary>
/// <remarks>
/// Pure, so the editor, the kiosk content and the tests all ask the same
/// question and get the same answer.
/// </remarks>
public static class DisplayMedia
{
    /// <summary>The header a kiosk sends to say it can play video.</summary>
    /// <remarks>
    /// A kiosk page left open through a deploy keeps running the old code,
    /// which shows every slide as an image. Handed a video it would draw a
    /// broken picture for the video's whole turn, on a shop-window screen,
    /// until someone reloaded it. So video slides go only to a kiosk that says
    /// it can play them; an old one keeps looping its posters.
    /// </remarks>
    public const string CapabilitiesHeader = "X-Display-Capabilities";

    /// <summary>Whether a kiosk's capability header includes video.</summary>
    public static bool CanPlayVideo(string? capabilities) =>
        !string.IsNullOrWhiteSpace(capabilities)
        && capabilities.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains("video", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a video may be on this pharmacy's screen: theirs, Ready, and
    /// with a finished file.
    /// </summary>
    /// <remarks>
    /// Checked when a playlist is saved <i>and</i> on every poll. A video that
    /// is deleted, fails, or is being re-cut drops off the screen on the next
    /// poll and comes back when it is Ready again — the playlist keeps it, the
    /// screen skips it.
    /// </remarks>
    public static bool IsShowable(Video? video, string ownerId) =>
        video is not null
        && video.OwnerId == ownerId
        && video.Status == VideoStatus.Ready
        && !string.IsNullOrWhiteSpace(video.BlobKey);

    /// <summary>How long a signing window lasts.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(6);

    /// <summary>
    /// When a video link signed at <paramref name="now"/> expires: the end of
    /// the current six-hour window, plus one more window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stable, not fresh.</b> The kiosk polls every thirty seconds and
    /// reloads when the content's revision changes. A link signed "now + N"
    /// would differ on every poll, change the revision, and restart the loop
    /// twice a minute. Rounding the expiry to a window gives every poll inside
    /// it the identical link.
    /// </para>
    /// <para>
    /// The extra window is the grace: when the link rotates, the kiosk still
    /// holds the old one until its next poll, and that old link is still good
    /// for at least six hours.
    /// </para>
    /// </remarks>
    public static DateTimeOffset LinkExpiry(DateTimeOffset now)
    {
        var windowTicks = Window.Ticks;
        var windowStart = now.UtcTicks - now.UtcTicks % windowTicks;
        return new DateTimeOffset(windowStart + 2 * windowTicks, TimeSpan.Zero);
    }

    /// <summary>
    /// The path a kiosk plays a video from: the web app's own route, carrying
    /// a signature bound to this one video and a window of time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never a blob key and never the screen's token. A <c>&lt;video&gt;</c>
    /// element cannot send a header, so the credential has to travel in the
    /// URL — which is why it is a narrow, expiring signature for one file,
    /// not the pairing token that unlocks the whole screen.
    /// </para>
    /// <para>
    /// <b><paramref name="version"/> is what makes caching safe.</b> The web
    /// route lets the screen's browser keep a video in its HTTP cache, so the
    /// second loop does not download it again. A re-cut writes the new MP4
    /// under the <i>same</i> blob key, so without a version in the URL a
    /// cached copy of the old cut would keep playing for hours. The version is
    /// the video's last-updated time: it moves when a render finishes (and on
    /// the odd edit such as a rename, which costs one re-download), and not
    /// while the video simply sits Ready on a screen.
    /// </para>
    /// </remarks>
    public static string VideoPath(string videoId, DateTimeOffset now, string? version = null)
    {
        var path = $"/api/display/video/{Uri.EscapeDataString(videoId)}?sig="
            + Uri.EscapeDataString(VideoDownloadToken.Sign(videoId, thumbnail: false, LinkExpiry(now)));
        return string.IsNullOrEmpty(version) ? path : $"{path}&v={Uri.EscapeDataString(version)}";
    }

    /// <summary>
    /// A short, stable token for one cut of a video: its last-updated time.
    /// </summary>
    public static string VersionOf(Video video) =>
        video.UpdatedAtUtc.Ticks.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
}
