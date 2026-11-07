using Acme.Application.Posters;
using Acme.Infrastructure.Export;
using Acme.Infrastructure.Options;
using Acme.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// The Instagram version of a poster. Instagram accepts only JPEG, and nothing
/// taller than 4:5; an A4 PNG poster is neither, so without this every poster
/// sent to Instagram was refused.
/// </summary>
public class InstagramImageTests
{
    private static readonly SkiaImageTranscoder Transcoder = new(
        Microsoft.Extensions.Options.Options.Create(new MessagingOptions()),
        NullLogger<SkiaImageTranscoder>.Instance);

    [Fact]
    public void An_A4_poster_becomes_a_4_by_5_jpeg_without_cropping()
    {
        var png = Poster(1240, 1754, SKColors.Navy, SKColors.Yellow);

        var jpeg = Transcoder.ToInstagramJpeg(png);

        Assert.NotNull(jpeg);
        using var codec = SKCodec.Create(new MemoryStream(jpeg!));
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
        Assert.Equal(SkiaImageTranscoder.InstagramWidthPx, codec.Info.Width);
        Assert.Equal(SkiaImageTranscoder.InstagramHeightPx, codec.Info.Height);

        // Fitted, not cropped: the poster's centre mark is still in the middle,
        // and the side margins are the poster's own background colour.
        using var bitmap = SKBitmap.Decode(jpeg);
        AssertClose(SKColors.Yellow, bitmap.GetPixel(540, 675));
        AssertClose(SKColors.Navy, bitmap.GetPixel(5, 675));
        AssertClose(SKColors.Navy, bitmap.GetPixel(1074, 675));
    }

    [Fact]
    public void Undecodable_bytes_give_null_not_an_exception()
    {
        Assert.Null(Transcoder.ToInstagramJpeg(new byte[] { 1, 2, 3 }));
        Assert.Null(Transcoder.ToInstagramJpeg(Array.Empty<byte>()));
    }

    [Fact]
    public void The_instagram_version_sits_beside_its_png_under_the_same_hash()
    {
        Assert.Equal("posters/o/p-abc-ig.jpg", PosterImageService.InstagramKey("posters/o/p-abc.png"));
    }

    [Theory]
    [InlineData("posters/o/p-abc-ig.jpg", "image/jpeg")]
    [InlineData("posters/o/p-abc.png", "image/png")]
    public void Stored_images_are_served_with_their_real_content_type(string key, string type)
    {
        Assert.Equal(type, AzurePosterImageStore.ContentTypeFor(key));
    }

    private static byte[] Poster(int w, int h, SKColor background, SKColor mark)
    {
        using var bitmap = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(background);
            using var paint = new SKPaint { Color = mark };
            canvas.DrawRect(SKRect.Create(w / 2f - 100, h / 2f - 100, 200, 200), paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void AssertClose(SKColor expected, SKColor actual)
    {
        // JPEG is lossy; a few levels either way is the same colour.
        Assert.InRange(Math.Abs(expected.Red - actual.Red), 0, 12);
        Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, 12);
        Assert.InRange(Math.Abs(expected.Blue - actual.Blue), 0, 12);
    }
}
