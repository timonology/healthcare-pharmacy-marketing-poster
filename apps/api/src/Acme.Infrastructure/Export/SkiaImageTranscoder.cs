using Acme.Application.Export;
using Acme.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Acme.Infrastructure.Export;

/// <inheritdoc cref="IImageTranscoder"/>
public sealed class SkiaImageTranscoder(
    IOptions<MessagingOptions> options,
    ILogger<SkiaImageTranscoder> log) : IImageTranscoder
{
    public byte[]? ToEmailJpeg(byte[] source)
    {
        if (source.Length == 0) return null;

        // Clamped rather than trusted. A width of zero from a mistyped
        // environment variable would scale the poster out of existence, and a
        // quality of 0 would produce something unreadable — both of which reach
        // patients before anyone notices.
        var m = options.Value;
        var maxWidthPx = Math.Clamp(m.PosterMaxWidthPx, 320, 4000);
        var quality = Math.Clamp(m.PosterJpegQuality, 40, 100);

        SKBitmap? decoded = null;
        try
        {
            // Decode throws rather than returning null for formats it has no
            // codec for — SVG raises ArgumentNullException("codec"), which is
            // how a vector logo once took a whole poster out of an email while
            // a null-check sat there looking correct. Catch, do not test.
            decoded = SKBitmap.Decode(source);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "Could not decode a {Bytes}-byte image to re-encode it; sending it unchanged.",
                source.Length);
            return null;
        }

        if (decoded is null) return null;

        try
        {
            var target = decoded;
            SKBitmap? scaled = null;

            if (decoded.Width > maxWidthPx)
            {
                var height = (int)Math.Round(decoded.Height * (double)maxWidthPx / decoded.Width);
                scaled = decoded.Resize(
                    new SKImageInfo(maxWidthPx, Math.Max(1, height)),
                    // High, not the default. A poster is mostly type, and a
                    // cheaper filter leaves small text visibly furred at the
                    // sizes an email uses. (SkiaSharp 2.88 — SKFilterQuality,
                    // not the SKSamplingOptions of 3.x.)
                    SKFilterQuality.High);

                if (scaled is not null) target = scaled;
            }

            try
            {
                using var image = SKImage.FromBitmap(target);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                if (data is null) return null;

                var bytes = data.ToArray();

                // Guard against the pathological case. JPEG beats PNG on
                // photographs and on the gradients posters tend to use, but a
                // flat two-colour design can encode smaller as PNG — and then
                // "optimising" it would make the email bigger.
                if (bytes.Length >= source.Length)
                {
                    log.LogInformation(
                        "JPEG re-encode was not smaller ({New:N0} vs {Old:N0} bytes); keeping the original.",
                        bytes.Length, source.Length);
                    return null;
                }

                log.LogInformation(
                    "Poster re-encoded for email: {Old:N0} -> {New:N0} bytes ({Pct:N0}% smaller), {W}px wide.",
                    source.Length, bytes.Length,
                    100.0 * (source.Length - bytes.Length) / source.Length, target.Width);

                return bytes;
            }
            finally
            {
                scaled?.Dispose();
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Re-encoding an image for email failed; sending it unchanged.");
            return null;
        }
        finally
        {
            decoded.Dispose();
        }
    }

    /// <summary>Instagram's 4:5 portrait feed frame.</summary>
    public const int InstagramWidthPx = 1080;
    public const int InstagramHeightPx = 1350;

    public byte[]? ToInstagramJpeg(byte[] source) =>
        FitToFrameJpeg(source, InstagramWidthPx, InstagramHeightPx);

    public byte[]? FitToFrameJpeg(byte[] source, int width, int height, bool whiteBackground = false)
    {
        if (source.Length == 0 || width < 1 || height < 1) return null;

        SKBitmap? decoded;
        try
        {
            decoded = SKBitmap.Decode(source);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not decode a {Bytes}-byte image to fit a frame.", source.Length);
            return null;
        }

        if (decoded is null) return null;

        try
        {
            // Fit, never fill: the whole image stays visible.
            var scale = Math.Min((double)width / decoded.Width, (double)height / decoded.Height);
            var w = Math.Max(1, (int)Math.Round(decoded.Width * scale));
            var h = Math.Max(1, (int)Math.Round(decoded.Height * scale));
            var x = (width - w) / 2f;
            var y = (height - h) / 2f;

            using var surface = SKSurface.Create(new SKImageInfo(
                width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return null;

            var canvas = surface.Canvas;
            canvas.Clear(whiteBackground ? SKColors.White : EdgeColour(decoded));

            using (var image = SKImage.FromBitmap(decoded))
            using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
            {
                canvas.DrawImage(image, SKRect.Create(x, y, w, h), paint);
            }

            canvas.Flush();

            using var snapshot = surface.Snapshot();
            // 90: the platforms recompress anyway, and starting from a heavily
            // compressed JPEG compounds the artefacts on small type.
            using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 90);
            return data?.ToArray();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Fitting an image to a {W}x{H} frame failed.", width, height);
            return null;
        }
        finally
        {
            decoded.Dispose();
        }
    }

    /// <summary>
    /// The average colour of the poster's left and right edges.
    /// </summary>
    /// <remarks>
    /// Posters nearly always have a flat or gently graded background at the
    /// edges, so this extends the design instead of boxing it. Transparent
    /// pixels count as white, which is what a viewer would see them as.
    /// </remarks>
    public static SKColor EdgeColour(SKBitmap bitmap)
    {
        long r = 0, g = 0, b = 0, n = 0;
        var step = Math.Max(1, bitmap.Height / 200);

        foreach (var col in new[] { 0, bitmap.Width - 1 })
        {
            for (var row = 0; row < bitmap.Height; row += step)
            {
                var c = bitmap.GetPixel(col, row);
                var a = c.Alpha / 255.0;
                r += (long)Math.Round(c.Red * a + 255 * (1 - a));
                g += (long)Math.Round(c.Green * a + 255 * (1 - a));
                b += (long)Math.Round(c.Blue * a + 255 * (1 - a));
                n++;
            }
        }

        return n == 0
            ? SKColors.White
            : new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }
}
