using Acme.Domain.Common;

namespace Acme.Domain.Videos;

/// <summary>Where a pharmacist's own picture came from.</summary>
public enum VideoImageSource
{
    /// <summary>A file they chose from their computer.</summary>
    Upload,

    /// <summary>Their brand kit's logo.</summary>
    Logo,

    /// <summary>A PNG of one of their published posters.</summary>
    Poster,
}

/// <summary>
/// A picture the pharmacy supplied, used as B-roll in its own video.
/// </summary>
/// <remarks>
/// <para>
/// <b>These never reach an image model.</b> They are the pharmacy's own
/// photographs and are pinned to particular beats of the script; the AI-imagery
/// toggle is a separate, paid feature. Sending a pharmacist's own photograph to
/// Replicate would charge them for a picture they already had.
/// </para>
/// <para>
/// A blob key rather than a URL, for the reason a canvas image shape stores one:
/// a video row is read for as long as the video exists, and a signed URL stops
/// working long before that.
/// </para>
/// </remarks>
public sealed record VideoImage
{
    public required string BlobKey { get; init; }
    public required VideoImageSource Source { get; init; }
    public string? Description { get; init; }

    /// <summary>
    /// Which part of the picture survives the crop to 9:16, as a point in 0–1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A point rather than a rectangle.</b> The frame is always 9:16 and the
    /// picture is always scaled to cover it, so the crop window's size is
    /// arithmetic — only its position is a decision. A rectangle would be three
    /// numbers that cannot disagree with the fourth, plus a rule for what to do
    /// when they did.
    /// </para>
    /// <para>
    /// Centre by default, which is what every picture got before this could be
    /// chosen. An old row with no focal point and a new one at 0.5 therefore
    /// render identically, and no migration is needed.
    /// </para>
    /// </remarks>
    public double FocalX { get; init; } = Centre;
    public double FocalY { get; init; } = Centre;

    /// <summary>
    /// How much of the picture fits in the frame. 1.0 is the cover crop every
    /// picture has always had; below shows more with a blurred fill; above
    /// punches in.
    /// </summary>
    /// <remarks>
    /// Mirrored as <c>ZOOM_MIN/ZOOM_MAX</c> in the worker's <c>framing.py</c>
    /// and <c>VIDEO_IMAGE_ZOOM_MIN/MAX</c> in shared-types; <c>VideoImageTests</c>
    /// reads all three. Absent on every row written before zoom existed, and
    /// read as 1.0 there — no migration, because 1.0 renders exactly what those
    /// rows always rendered.
    /// </remarks>
    public double Zoom { get; init; } = ZoomDefault;

    public const double Centre = 0.5;
    public const double ZoomDefault = 1.0;
    public const double ZoomMin = 0.5;
    public const double ZoomMax = 1.5;

    public static VideoImage Create(
        string blobKey, VideoImageSource source, string? description,
        double focalX = Centre, double focalY = Centre, double zoom = ZoomDefault)
    {
        if (string.IsNullOrWhiteSpace(blobKey))
            throw new DomainException("An attached image needs a blob key.");

        return new VideoImage
        {
            BlobKey = blobKey.Trim(),
            Source = source,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            // Clamped rather than refused. This arrives from a drag in a
            // browser, so a value a hair outside the box is a rounding artefact
            // of the pharmacist's own gesture, not an attempt at anything — and
            // refusing their whole video over it would be absurd.
            FocalX = Clamp(focalX),
            FocalY = Clamp(focalY),
            Zoom = ClampZoom(zoom),
        };
    }

    private static double Clamp(double v) =>
        double.IsNaN(v) ? Centre : Math.Clamp(v, 0d, 1d);

    /// <summary>Within bounds; 1.0 for anything that is not a real number.</summary>
    /// <remarks>
    /// Clamped rather than refused, like the focal point: this arrives from a
    /// slider and a pinch, and a value a hair outside the range is the gesture,
    /// not an attack. Infinity is treated as unreadable rather than clamped to
    /// the maximum — nobody chose it, so punching in is not what they meant.
    /// </remarks>
    public static double ClampZoom(double v) =>
        double.IsNaN(v) || double.IsInfinity(v) ? ZoomDefault : Math.Clamp(v, ZoomMin, ZoomMax);
}
