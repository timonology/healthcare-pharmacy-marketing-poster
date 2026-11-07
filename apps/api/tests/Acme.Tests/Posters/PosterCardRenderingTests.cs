using System.Text.Json.Nodes;
using Acme.Application.BrandKit;
using Acme.Application.Posters;
using Acme.Infrastructure.Export;
using Acme.Infrastructure.Options;
using Acme.Infrastructure.Persistence;
using Acme.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Acme.Tests.Posters;

/// <summary>
/// The My Posters card is drawn by the PDF/PNG renderer, and is never empty.
/// </summary>
/// <remarks>
/// <para>
/// From an incident: "Advice checklist" and "Child Health Clinic" showed a
/// blank card with the browser's "?" while other posters on the page drew.
/// The card linked to a stored render through a signed storage URL, which the
/// browser could not reach. Cards now come from
/// <see cref="PosterImageService.RenderCardAsync"/>; these run it on real
/// canvases with the real renderer and transcoder. Nothing is mocked except
/// storage, which serves the house photographs from the embedded copies the
/// seeder uploads.
/// </para>
/// </remarks>
public class PosterCardRenderingTests
{
    private const string AdviceChecklist = "sys-house-advice-checklist";

    private static readonly SKColor Navy = SKColor.Parse("#0b2e59");
    private static readonly SKColor Placeholder = SKColor.Parse("#e5e7eb");

    private static readonly SkiaImageTranscoder Transcoder =
        new(Microsoft.Extensions.Options.Options.Create(new MessagingOptions()), NullLogger<SkiaImageTranscoder>.Instance);

    private static QuestPdfExporter Renderer() =>
        new(NullLogger<QuestPdfExporter>.Instance, new EmbeddedAssets());

    private static Task<byte[]> Card(string canvasJson) =>
        PosterImageService.RenderCardAsync(Renderer(), Transcoder, canvasJson, watermark: false, default);

    /// <summary>
    /// A house template as a poster's canvas, resolved for a pharmacy with no
    /// brand kit — so no logo and, importantly, no booking link.
    /// </summary>
    private static string HouseCanvas(string templateId, Action<JsonArray>? edit = null)
    {
        using var stream = typeof(TemplateSeeder).Assembly
            .GetManifestResourceStream("HouseTemplates.templates.json")!;
        var template = JsonNode.Parse(stream)!.AsArray()
            .Single(t => t!["id"]!.GetValue<string>() == templateId)!;

        var shapes = template["shapes"]!.DeepClone().AsArray();
        edit?.Invoke(shapes);

        var canvas = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["width"] = 1240,
            ["height"] = 1754,
            ["background"] = template["background"]!.GetValue<string>(),
            ["shapes"] = shapes,
        };
        return BrandCanvasAssets.ResolveForRender(canvas.ToJsonString(), kit: null);
    }

    [Fact]
    public async Task The_advice_checklist_card_is_a_real_picture_not_an_empty_image()
    {
        var bytes = await Card(HouseCanvas(AdviceChecklist));

        bytes.Should().NotBeEmpty("an empty card is the bug");
        using var card = SKBitmap.Decode(bytes);
        card.Should().NotBeNull("the card must be an image a browser can draw");

        card!.Width.Should().Be(PosterImageService.CardWidthPx);
        card.Height.Should().Be((int)Math.Round(1754 * (PosterImageService.CardWidthPx / 1240f)));

        // Not one flat colour: a good part of the card differs from the
        // background, as it must with a photograph, cards and a footer bar.
        var background = SKColor.Parse("#e8f5fb");
        FractionNotNear(card, new SKRectI(0, 0, card.Width, card.Height), background)
            .Should().BeGreaterThan(0.25, "the card shows the design, not its background");

        // The photograph is drawn (a photo varies; a placeholder or a gap does not).
        LuminanceSpread(card, Scaled(card, 600, 0, 640, 1200))
            .Should().BeGreaterThan(20, "the photograph must be on the card");

        // The navy footer bar is there, in its own colour.
        IsNear(card.GetPixel(5, card.Height - 5), Navy)
            .Should().BeTrue("the footer bar is drawn");
    }

    [Fact]
    public async Task No_booking_link_means_no_QR_and_no_grey_box()
    {
        using var card = SKBitmap.Decode(await Card(HouseCanvas(AdviceChecklist)))!;

        // The QR slot sits on the white contact card. Without a booking link it
        // must stay white — not the renderer's grey "missing image" box.
        FractionNear(card, Scaled(card, 670, 1425, 170, 170), Placeholder)
            .Should().BeLessThan(0.1, "a pharmacy with no booking link gets no code, not a grey square");
    }

    [Fact]
    public async Task A_missing_photo_costs_its_own_slot_not_the_whole_card()
    {
        var canvas = HouseCanvas(AdviceChecklist, shapes =>
        {
            var photo = shapes.Single(s => s!["id"]!.GetValue<string>() == "photo")!;
            photo["blobKey"] = "/api/assets/templates/00000000000000000000000000000000.jpg";
        });

        var bytes = await Card(canvas);
        using var card = SKBitmap.Decode(bytes)!;

        IsNear(card.GetPixel(5, card.Height - 5), Navy)
            .Should().BeTrue("everything else on the poster still renders");
        FractionNotNear(card, Scaled(card, 90, 1390, 780, 240), SKColor.Parse("#e8f5fb"))
            .Should().BeGreaterThan(0.5, "the contact card still renders");
    }

    [Fact]
    public async Task Waves_and_diagonals_are_filled()
    {
        // A closed line is a filled shape (a wave footer, a diagonal split);
        // drawn as an outline it leaves a hairline and the page shows through.
        var canvas = """
        {"width":1240,"height":1754,"background":"#ffffff","shapes":[
          {"id":"diag","kind":"line","position":{"x":0,"y":0},"rotation":0,"scale":{"x":1,"y":1},
           "opacity":1,"zIndex":1,"points":[0,900,1240,500,1240,1754,0,1754],"closed":true,
           "stroke":"#0fa3b1","strokeWidth":1}
        ]}
        """;
        using var card = SKBitmap.Decode(await Card(canvas))!;

        IsNear(card.GetPixel(card.Width / 2, card.Height - 40), SKColor.Parse("#0fa3b1"))
            .Should().BeTrue("the inside of a closed shape is filled with its colour");
        IsNear(card.GetPixel(card.Width / 2, 40), SKColors.White)
            .Should().BeTrue("and only the inside");
    }

    [Fact]
    public async Task Circle_cropped_photos_keep_their_transparent_corners()
    {
        // A circle portrait is a square PNG, transparent outside the circle.
        var png = CirclePng(200, SKColors.Red);
        var canvas = $$"""
        {"width":1240,"height":1754,"background":"#0b2e59","shapes":[
          {"id":"portrait","kind":"image","position":{"x":420,"y":400},"rotation":0,"scale":{"x":1,"y":1},
           "opacity":1,"zIndex":1,"width":400,"height":400,
           "blobKey":"data:image/png;base64,{{Convert.ToBase64String(png)}}"}
        ]}
        """;
        using var card = SKBitmap.Decode(await Card(canvas))!;
        var s = PosterImageService.CardWidthPx / 1240f;

        IsNear(card.GetPixel((int)(620 * s), (int)(600 * s)), SKColors.Red)
            .Should().BeTrue("the circle is drawn");
        IsNear(card.GetPixel((int)(430 * s), (int)(410 * s)), Navy)
            .Should().BeTrue("the corner outside the circle shows the poster, not a white or black square");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static byte[] CirclePng(int size, SKColor colour)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = colour, IsAntialias = true };
        surface.Canvas.DrawCircle(size / 2f, size / 2f, size / 2f, paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A canvas-space rectangle, in card pixels.</summary>
    private static SKRectI Scaled(SKBitmap card, float x, float y, float w, float h)
    {
        var s = card.Width / 1240f;
        return SKRectI.Create((int)(x * s), (int)(y * s), Math.Max(1, (int)(w * s)), Math.Max(1, (int)(h * s)));
    }

    /// <summary>JPEG moves colours a little; this much is still "the same colour".</summary>
    private static bool IsNear(SKColor a, SKColor b, int tolerance = 18) =>
        Math.Abs(a.Red - b.Red) <= tolerance
        && Math.Abs(a.Green - b.Green) <= tolerance
        && Math.Abs(a.Blue - b.Blue) <= tolerance;

    private static IEnumerable<SKColor> Pixels(SKBitmap card, SKRectI area)
    {
        area.Intersect(new SKRectI(0, 0, card.Width, card.Height));
        for (var y = area.Top; y < area.Bottom; y += 2)
            for (var x = area.Left; x < area.Right; x += 2)
                yield return card.GetPixel(x, y);
    }

    private static double FractionNear(SKBitmap card, SKRectI area, SKColor colour)
    {
        var all = Pixels(card, area).ToList();
        return all.Count == 0 ? 0 : all.Count(p => IsNear(p, colour, 6)) / (double)all.Count;
    }

    private static double FractionNotNear(SKBitmap card, SKRectI area, SKColor colour)
    {
        var all = Pixels(card, area).ToList();
        return all.Count == 0 ? 0 : all.Count(p => !IsNear(p, colour, 10)) / (double)all.Count;
    }

    /// <summary>Standard deviation of luminance across an area.</summary>
    private static double LuminanceSpread(SKBitmap card, SKRectI area)
    {
        var l = Pixels(card, area).Select(p => 0.2126 * p.Red + 0.7152 * p.Green + 0.0722 * p.Blue).ToList();
        if (l.Count == 0) return 0;
        var mean = l.Average();
        return Math.Sqrt(l.Sum(v => (v - mean) * (v - mean)) / l.Count);
    }

    /// <summary>
    /// Storage holding only the house photographs, from the copies embedded in
    /// Acme.Infrastructure — exactly what the seeder uploads.
    /// </summary>
    private sealed class EmbeddedAssets : IBlobStorageService
    {
        public Task<Stream> DownloadAsync(string blobKey, CancellationToken ct)
        {
            var prefix = TemplateAssetUrls.BlobPrefix + "/";
            var stream = blobKey.StartsWith(prefix, StringComparison.Ordinal)
                ? TemplateSeeder.OpenEmbeddedAsset(blobKey[prefix.Length..])
                : null;
            return stream is not null
                ? Task.FromResult(stream)
                : Task.FromException<Stream>(new FileNotFoundException("No such blob", blobKey));
        }

        public Task<string> UploadAsync(string blobKey, Stream content, string contentType, CancellationToken ct) =>
            throw new NotSupportedException("rendering a card must not write to storage");
        public Task DeleteAsync(string blobKey, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Uri GetReadOnlyUri(string blobKey, TimeSpan validFor) =>
            new("https://storage.invalid/" + blobKey);
    }
}
