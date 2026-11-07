namespace Acme.Application.Export;

/// <summary>
/// Re-encodes an already-rendered image for a destination with a byte budget.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="ICanvasRasterizer"/> on purpose. Rendering a canvas
/// is expensive and its output is cached and shared — the same PNG serves the
/// kiosk, social publishing and the campaign email. Re-encoding is cheap, is
/// wanted by exactly one of those consumers, and must not disturb the cache
/// key. So the render stays as it is and the email transcodes at its own
/// boundary.
/// </para>
/// <para>
/// The cost this exists to remove: a poster renders to a 1240px PNG at quality
/// 100, which is about 1.2 MB, and the email displays it at 600px.
/// </para>
/// <para>
/// It used to run in the sender, because base64 in a mail API request made that
/// 1.6 MB and Mailjet carried an inline image inside <i>each</i> recipient's
/// copy, so the figure multiplied by the audience. The poster is a link now and
/// this runs where the link is served, once per reader who looks rather than
/// once per recipient either way. Same call, and the reason for it is now
/// about the reader's connection instead of the sender's.
/// </para>
/// </remarks>
public interface IImageTranscoder
{
    /// <summary>
    /// Re-encodes a rendered poster to something an email should carry.
    /// </summary>
    /// <remarks>
    /// No width or quality argument, deliberately. Those are deployment
    /// settings — a pharmacy on a slow uplink wants a smaller poster than one
    /// on fibre, and that is not a decision a campaign makes per send. The
    /// implementation reads them from configuration
    /// (<c>Messaging:PosterMaxWidthPx</c>, <c>Messaging:PosterJpegQuality</c>),
    /// which also keeps this interface free of a dependency on the messaging
    /// options that live in Infrastructure.
    /// </remarks>
    /// <returns>
    /// The JPEG bytes, or <see langword="null"/> if the source could not be
    /// decoded or the result was no smaller. <b>Null means "use the original"</b>,
    /// never "drop the image" — a campaign that silently loses its poster to a
    /// failed optimisation is a worse outcome than one that sends a larger file.
    /// </returns>
    byte[]? ToEmailJpeg(byte[] source);

    /// <summary>
    /// Fits a rendered poster into an Instagram feed image.
    /// </summary>
    /// <remarks>
    /// Instagram accepts only JPEG, and only aspect ratios from 4:5 to 1.91:1.
    /// A poster is A4 (about 1:1.41), taller than anything Instagram allows, so
    /// it is scaled to fit a 1080×1350 (4:5) frame — the tallest allowed, which
    /// wastes the least space — and centred, with the sides filled in the
    /// poster's own edge colour so it reads as one image rather than a poster
    /// on a card. Never cropped: cropping an A4 design to 4:5 cuts off the
    /// headline or the contact line.
    /// </remarks>
    /// <returns>JPEG bytes, or null when the source cannot be decoded.</returns>
    byte[]? ToInstagramJpeg(byte[] source);

    /// <summary>
    /// Fits an image whole inside a fixed frame and encodes it as JPEG — the
    /// shapes ad platforms insist on (Google's 1.91:1 and 1:1, Instagram's 4:5).
    /// </summary>
    /// <param name="whiteBackground">
    /// Fill the margins white (right for a logo) rather than with the image's
    /// own edge colour (right for a poster, which then reads as one picture).
    /// </param>
    /// <returns>JPEG bytes, or null when the source cannot be decoded.</returns>
    byte[]? FitToFrameJpeg(byte[] source, int width, int height, bool whiteBackground = false);
}
