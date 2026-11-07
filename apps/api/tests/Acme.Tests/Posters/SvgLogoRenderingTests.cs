using System.Text;
using Acme.Application.Export;
using Acme.Infrastructure.Export;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Acme.Tests.Posters;

/// <summary>
/// A poster survives an image the renderer cannot decode — and renders an SVG.
/// </summary>
/// <remarks>
/// <para>
/// Both halves come from one incident. A pharmacy uploaded an SVG logo, applied
/// it to a poster, and sent a campaign; the patient received an email with no
/// poster in it at all. The log said:
/// </para>
/// <code>
/// System.ArgumentNullException: Value cannot be null. (Parameter 'codec')
///    at SkiaSharp.SKBitmap.Decode(SKCodec codec)
/// </code>
/// <para>
/// Two separate defects. <c>SKBitmap.Decode</c> cannot read SVG, which is the
/// visible one — but the render also had a null check meant to skip an
/// undecodable image, and it never ran, because Decode throws rather than
/// returning null for these bytes. So a single unsupported logo cost the entire
/// poster instead of just itself.
/// </para>
/// <para>
/// These use data-URIs, so nothing is mocked and no storage is involved: the
/// exporter's own loader reads them, exactly as it does in production.
/// </para>
/// </remarks>
public class SvgLogoRenderingTests
{
    private static QuestPdfExporter NewExporter() =>
        new(NullLogger<QuestPdfExporter>.Instance);

    /// <summary>A solid red square, as an SVG data-URI.</summary>
    private static string RedSquareSvgDataUri()
    {
        const string svg =
            """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100">"""
            + """<rect width="100" height="100" fill="#FF0000"/></svg>""";

        return "data:image/svg+xml;base64,"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }

    /// <summary>A canvas with one image shape covering a known rectangle.</summary>
    private static string CanvasWith(string blobKey) =>
        $$"""
        {
          "width": 400,
          "height": 400,
          "background": "#FFFFFF",
          "shapes": [
            {
              "id": "logo",
              "kind": "image",
              "position": { "x": 100, "y": 100 },
              "width": 200,
              "height": 200,
              "zIndex": 1,
              "blobKey": "{{blobKey}}"
            }
          ]
        }
        """;

    private static SKColor PixelAt(byte[] png, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(png);
        bitmap.Should().NotBeNull("the render must produce a decodable PNG");
        return bitmap!.GetPixel(x, y);
    }

    [Fact]
    public async Task An_svg_logo_is_rasterised_and_drawn()
    {
        var png = await NewExporter().RenderPngAsync(
            new RasterRequest(CanvasWith(RedSquareSvgDataUri()), 400, false),
            CancellationToken.None);

        png.Should().NotBeEmpty();

        // The centre of the poster is the centre of the logo. Asserting the
        // colour, not just that bytes came back: an exporter that silently
        // skipped the image would still return a perfectly valid white PNG,
        // which is precisely the bug wearing a disguise.
        var centre = PixelAt(png, 200, 200);

        centre.Red.Should().BeGreaterThan(200, "the logo is a solid red square");
        centre.Green.Should().BeLessThan(60);
        centre.Blue.Should().BeLessThan(60);
    }

    [Fact]
    public async Task An_svg_logo_does_not_bleed_outside_its_shape()
    {
        // The rasteriser translates by the picture's cull rect and scales to
        // fit. Getting either wrong puts the logo in the wrong place, which a
        // single centre-pixel check would not notice.
        var png = await NewExporter().RenderPngAsync(
            new RasterRequest(CanvasWith(RedSquareSvgDataUri()), 400, false),
            CancellationToken.None);

        var corner = PixelAt(png, 20, 20);

        corner.Red.Should().BeGreaterThan(200);
        corner.Green.Should().BeGreaterThan(200);
        corner.Blue.Should().BeGreaterThan(200,
            "outside the shape the canvas background shows through");
    }

    [Fact]
    public async Task An_undecodable_image_costs_only_itself()
    {
        // Bytes that are not an image in any format. Before the fix this threw
        // out of the renderer and the caller turned it into "this poster could
        // not be turned into an image" — so the whole design was lost, and with
        // it the campaign's only content.
        var junk = "data:image/png;base64,"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes("not an image at all"));

        var render = async () => await NewExporter().RenderPngAsync(
            new RasterRequest(CanvasWith(junk), 400, false),
            CancellationToken.None);

        await render.Should().NotThrowAsync(
            "one unreadable image must never cost the whole poster");

        var png = await NewExporter().RenderPngAsync(
            new RasterRequest(CanvasWith(junk), 400, false),
            CancellationToken.None);

        png.Should().NotBeEmpty();
        PixelAt(png, 200, 200).Blue.Should().BeGreaterThan(200,
            "the rest of the poster still renders — here, the background");
    }

    [Fact]
    public async Task An_empty_source_draws_nothing_at_all()
    {
        // What a booking QR becomes for a pharmacy with no booking link, and a
        // logo slot for one with no logo. It printed as a grey box — the
        // placeholder meant for a photo that failed to load — so every poster
        // with a QR slot carried a blank square for those pharmacies.
        var png = await NewExporter().RenderPngAsync(
            new RasterRequest(CanvasWith(""), 400, false),
            CancellationToken.None);

        var centre = PixelAt(png, 200, 200);
        centre.Red.Should().BeGreaterThan(250, "nothing is drawn: the white background shows");
        centre.Green.Should().BeGreaterThan(250);
        centre.Blue.Should().BeGreaterThan(250);
    }

    [Fact]
    public async Task A_source_that_fails_to_load_still_shows_its_placeholder()
    {
        // The other half: a real source that cannot be read keeps a visible
        // box, so a broken photo is noticed rather than silently dropped.
        var png = await NewExporter().RenderPngAsync(
            new RasterRequest(CanvasWith("/api/assets/templates/00000000000000000000000000000000.png"), 400, false),
            CancellationToken.None);

        var centre = PixelAt(png, 200, 200);
        centre.Red.Should().BeLessThan(245, "the grey placeholder (#e5e7eb), not the white page");
    }

    [Fact]
    public async Task A_png_logo_still_renders()
    {
        // The SVG path must not have disturbed the ordinary one. Built by
        // encoding a red bitmap, so this is a real PNG rather than a fixture
        // that might drift from what Skia actually accepts.
        using var bitmap = new SKBitmap(100, 100);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(255, 0, 0));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var uri = "data:image/png;base64," + Convert.ToBase64String(data.ToArray());

        var png = await NewExporter().RenderPngAsync(
            new RasterRequest(CanvasWith(uri), 400, false),
            CancellationToken.None);

        PixelAt(png, 200, 200).Red.Should().BeGreaterThan(200);
    }
}
