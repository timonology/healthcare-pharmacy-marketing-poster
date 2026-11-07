namespace Acme.Application.BrandKit;

/// <summary>
/// Turns a booking link into a QR code a patient can scan off a wall.
/// </summary>
/// <remarks>
/// <para>
/// An interface rather than a direct call, for the reason
/// <see cref="Acme.Application.Export.IImageTranscoder"/> is one: encoding a QR
/// needs a third-party library, and a library belongs in Infrastructure. The
/// Application layer states what it wants and Infrastructure decides what to
/// pull in.
/// </para>
/// <para>
/// <b>The output has to be deterministic for a given link.</b> The resolved
/// canvas — QR data URL and all — is hashed into the poster render's cache key,
/// so an encoder that varied its bytes between calls for the same input would
/// change the key on every render and re-rasterise every poster, every time,
/// forever. Nothing would look wrong; it would just be slow in a way nobody
/// would trace back to here.
/// </para>
/// </remarks>
public interface IQrCodeGenerator
{
    /// <summary>
    /// A PNG of <paramref name="content"/>, or null if it cannot be encoded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PNG rather than SVG because the canvas renderers already load images by
    /// data URI and a QR is a grid of hard-edged squares — the format it scales
    /// badly in is the one nobody uses it in. It is drawn at a fixed size on a
    /// poster and printed.
    /// </para>
    /// <para>
    /// Null rather than an exception, and null means "no QR on this poster". A
    /// booking link that cannot be encoded is a design with a gap in it; a
    /// throw here would take the whole export down and a pharmacist would lose
    /// a poster over a URL.
    /// </para>
    /// </remarks>
    /// <param name="content">The booking link, already normalised and absolute.</param>
    /// <param name="pixelsPerModule">
    /// How many image pixels each QR square becomes. Drives the output size —
    /// a typical booking URL is a 33×33 module grid, so 8 gives roughly 300px
    /// including the mandatory quiet zone.
    /// </param>
    byte[]? ToPng(string content, int pixelsPerModule = 8);
}
