using Acme.Application.BrandKit;
using Microsoft.Extensions.Logging;
using QRCoder;

namespace Acme.Infrastructure.BrandKit;

/// <summary>
/// QR encoding, via QRCoder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not hand-rolled, deliberately.</b> A QR encoder is Reed-Solomon error
/// correction, eight candidate mask patterns scored against a penalty rule, and
/// format and version bit-strings with their own BCH codes. A subtly wrong
/// implementation still produces something that looks exactly like a QR code —
/// it just does not scan, or scans as the wrong thing, and the failure is
/// discovered on a wall in a pharmacy after the posters are printed.
/// </para>
/// <para>
/// <c>PngByteQRCode</c> specifically, out of the renderers QRCoder ships: it is
/// pure managed. The others reach for System.Drawing, which needs libgdiplus
/// and would not survive the Linux container this runs in.
/// </para>
/// <para>
/// Error correction level Q — about 25% of the code can be obscured and still
/// read. Higher than the usual default of M because of where these end up: a
/// window at an angle, behind reflective glass, on a poster somebody has stuck
/// a sticker on. The cost is a denser grid, which matters only if the booking
/// link is very long.
/// </para>
/// </remarks>
public sealed class QrCoderGenerator(ILogger<QrCoderGenerator> log) : IQrCodeGenerator
{
    public byte[]? ToPng(string content, int pixelsPerModule = 8)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        // Clamped rather than trusted. This is reached from a render path, and a
        // silly value is either an unreadable speck or a hundred-megabyte PNG
        // held in memory while QuestPDF lays out a page.
        var scale = Math.Clamp(pixelsPerModule, 2, 32);

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
            using var png = new PngByteQRCode(data);
            return png.GetGraphic(scale);
        }
        catch (Exception ex)
        {
            // Never fatal. A poster without its QR is a poster; an export that
            // throws is a pharmacist who cannot print anything and does not know
            // why. The log is how anyone finds out it happened.
            log.LogWarning(ex,
                "Could not encode a QR code for a {Length}-character booking link. "
                + "The poster will render without it.", content.Length);
            return null;
        }
    }
}
