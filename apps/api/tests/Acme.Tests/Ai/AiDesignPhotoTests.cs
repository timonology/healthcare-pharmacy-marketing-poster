using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Acme.Application.BrandKit;
using Acme.Application.Templates;
using Acme.Infrastructure.Options;
using Acme.Infrastructure.Storage;
using Acme.Infrastructure.Templates;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acme.Tests.Ai;

/// <summary>
/// The photo side of "Design with AI": the choices, the file check, the logo
/// placed after generation, and the baked photograph stored by the API.
/// </summary>
public class AiDesignPhotoTests
{
    /* ── what a file really is ───────────────────────────────────────── */

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, "image/png")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4,
                             (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp")]
    public void Real_images_are_recognised_by_their_bytes(byte[] head, string expected) =>
        ImageSniffer.Detect(head).Should().Be(expected);

    [Theory]
    [InlineData("%PDF-1.4 renamed to photo.png")]
    [InlineData("GIF89a not accepted")]
    [InlineData("")]
    public void Anything_else_is_refused_whatever_it_is_called(string content) =>
        ImageSniffer.Detect(Encoding.ASCII.GetBytes(content)).Should().BeNull();

    /* ── the photo choice ─────────────────────────────────────────────── */

    private static bool Ours(string url) => TemplateAssetUrls.TryResolveBlobKey(url) is not null;
    private const string OurAsset = "/api/assets/templates/0123456789abcdef0123456789abcdef.jpg";

    [Fact]
    public void Stock_with_nothing_picked_and_none_are_always_fine()
    {
        new PosterPhoto(PosterPhotoMode.Stock).Problem(Ours).Should().BeNull();
        new PosterPhoto(PosterPhotoMode.None).Problem(Ours).Should().BeNull();
        new PosterPhoto(PosterPhotoMode.Stock, StockPhotoId: "4242").Problem(Ours).Should().BeNull();
    }

    [Fact]
    public void An_upload_must_be_one_of_our_own_assets()
    {
        new PosterPhoto(PosterPhotoMode.Upload, AssetUrl: OurAsset).Problem(Ours).Should().BeNull();
        new PosterPhoto(PosterPhotoMode.Upload).Problem(Ours).Should().Contain("Upload a photo");
        // Never a URL a caller typed: the designer is not pointed at the internet.
        new PosterPhoto(PosterPhotoMode.Upload, AssetUrl: "https://evil.example/x.jpg")
            .Problem(Ours).Should().Contain("not uploaded here");
        new PosterPhoto(PosterPhotoMode.Upload, AssetUrl: "/api/assets/templates/../secret.jpg")
            .Problem(Ours).Should().NotBeNull();
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("https://images.pexels.com/1.jpg")]
    [InlineData("123456789012345678901")]
    public void A_stock_id_is_an_id_not_a_url(string id) =>
        new PosterPhoto(PosterPhotoMode.Stock, StockPhotoId: id).Problem(Ours).Should().NotBeNull();

    [Fact]
    public void Ai_needs_a_description()
    {
        new PosterPhoto(PosterPhotoMode.Ai, Description: "ab").Problem(Ours).Should().Contain("Describe");
        new PosterPhoto(PosterPhotoMode.Ai, Description: "a nurse giving a flu jab").Problem(Ours).Should().BeNull();
    }

    [Fact]
    public void A_description_is_bounded() =>
        new PosterPhoto(PosterPhotoMode.Upload, AssetUrl: OurAsset, Description: new string('x', 301))
            .Problem(Ours).Should().Contain("300");

    /* ── the logo, after generation ──────────────────────────────────── */

    private const string Canvas = """
        {"width":1240,"height":1754,"background":"#ffffff","shapes":[
          {"id":"headline","kind":"text","zIndex":3,"text":"Hi"},
          {"id":"pharmacy-booking-qr","kind":"image","zIndex":7,"blobKey":"/api/brand-kit/booking-qr"}]}
        """;

    [Fact]
    public void The_logo_goes_where_the_house_style_leaves_room_for_it()
    {
        var doc = JsonNode.Parse(BrandCanvasAssets.PlaceLogo(Canvas))!.AsObject();
        var logo = doc["shapes"]!.AsArray().Single(s => s!["id"]!.GetValue<string>() == "pharmacy-logo")!;

        logo["blobKey"]!.GetValue<string>().Should().Be(BrandCanvasAssets.SrcPath);
        // apply-pharmacy.ts: 16% wide, half as tall, 80px in, 90px up.
        logo["width"]!.GetValue<double>().Should().Be(198);
        logo["height"]!.GetValue<double>().Should().Be(99);
        logo["position"]!["x"]!.GetValue<double>().Should().Be(1240 - 198 - 80);
        logo["position"]!["y"]!.GetValue<double>().Should().Be(1754 - 99 - 90);
        logo["zIndex"]!.GetValue<double>().Should().Be(8, "drawn above everything else");
    }

    [Fact]
    public void Placing_the_logo_twice_places_it_once()
    {
        var once = BrandCanvasAssets.PlaceLogo(Canvas);
        BrandCanvasAssets.PlaceLogo(once).Should().Be(once);
    }

    [Fact]
    public void A_design_with_its_own_logo_slot_keeps_it() =>
        BrandCanvasAssets.PlaceLogo("""
            {"width":1240,"height":1754,"shapes":[{"id":"corner","kind":"image","blobKey":"/api/brand-kit/logo","zIndex":1}]}
            """).Should().NotContain("pharmacy-logo");

    /* ── the generator stores the baked photo ────────────────────────── */

    [Fact]
    public void Only_image_shapes_have_their_placeholder_swapped()
    {
        const string json = """
            {"name":"asset:photo","shapes":[
              {"id":"photo","kind":"image","blobKey":"asset:photo"},
              {"id":"note","kind":"text","text":"asset:photo"}]}
            """;
        var swapped = JsonNode.Parse(AiTemplateGenerator.ReplaceImageKey(json, "asset:photo", OurAsset))!;

        swapped["shapes"]![0]!["blobKey"]!.GetValue<string>().Should().Be(OurAsset);
        swapped["shapes"]![1]!["text"]!.GetValue<string>().Should().Be("asset:photo");
        swapped["name"]!.GetValue<string>().Should().Be("asset:photo");
    }

    [Fact]
    public async Task An_upload_travels_as_bytes_and_the_baked_photo_comes_back_stored()
    {
        var store = new MemoryAssets();
        var uploaded = await store.UploadAsync(new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF, 1, 2, 3 }), "image/jpeg", "", default);

        var baked = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 9 });
        var handler = new Handler($$"""
            {"canvas":{"name":"Trusted","shapes":[{"id":"photo","kind":"image","blobKey":"asset:photo"}]},
             "name":"Trusted","notes":[],
             "assets":[{"key":"asset:photo","data_base64":"{{baked}}","content_type":"image/png",
                        "width":660,"height":660,"source":"upload"}]}
            """);
        var generator = Generator(handler, store);

        var result = await generator.GenerateAsync(
            new GenerateTemplateRequest("trusted local pharmacy",
                Photo: new PosterPhoto(PosterPhotoMode.Upload, uploaded.Url, Description: "our pharmacist")),
            "token", default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var sent = JsonNode.Parse(handler.Body!)!["photo"]!;
        sent["mode"]!.GetValue<string>().Should().Be("upload");
        Convert.FromBase64String(sent["data_base64"]!.GetValue<string>())
            .Should().Equal(0xFF, 0xD8, 0xFF, 1, 2, 3);
        sent["description"]!.GetValue<string>().Should().Be("our pharmacist");
        handler.Body.Should().NotContain(uploaded.Url, "ai-service is sent bytes, never a URL to fetch");

        // The baked PNG was stored, and the canvas now points at it.
        var stored = store.Files.Keys.Single(k => k.EndsWith(".png"));
        result.Value!.CanvasJson.Should().Contain(TemplateAssetUrls.UrlFor(stored))
            .And.NotContain("asset:photo");
    }

    [Theory]
    [InlineData(PosterPhotoMode.Stock, "4242", "stock")]
    [InlineData(PosterPhotoMode.Ai, null, "ai")]
    [InlineData(PosterPhotoMode.None, null, "none")]
    public async Task Each_mode_is_sent_as_ai_service_expects(PosterPhotoMode mode, string? id, string wire)
    {
        var handler = new Handler("""{"canvas":{"shapes":[]},"name":"x","notes":[],"assets":[]}""");
        var result = await Generator(handler, new MemoryAssets()).GenerateAsync(
            new GenerateTemplateRequest("walk-in flu clinic, bold",
                Photo: new PosterPhoto(mode, StockPhotoId: id, Description: "a nurse")),
            "token", default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var sent = JsonNode.Parse(handler.Body!)!["photo"]!;
        sent["mode"]!.GetValue<string>().Should().Be(wire);
        if (id is not null) sent["stock_photo_id"]!.GetValue<string>().Should().Be(id);
    }

    [Fact]
    public async Task A_missing_upload_is_refused_before_ai_service_is_called()
    {
        var handler = new Handler("{}");
        var result = await Generator(handler, new MemoryAssets()).GenerateAsync(
            new GenerateTemplateRequest("trusted local pharmacy",
                Photo: new PosterPhoto(PosterPhotoMode.Upload, OurAsset)),
            "token", default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("no longer available");
        handler.Body.Should().BeNull();
    }

    /* ── a pharmacy's upload ──────────────────────────────────────────── */

    private static Application.Ai.PosterPhotoService Photos(MemoryUploads store) =>
        new(null!, store, NullLogger<Application.Ai.PosterPhotoService>.Instance);

    [Fact]
    public async Task A_real_photo_is_stored_under_the_type_its_bytes_say()
    {
        var store = new MemoryUploads();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5 };

        var result = await Photos(store).UploadAsync("owner-1", new MemoryStream(png), png.Length, default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.ContentType.Should().Be("image/png");
        // Private and per pharmacy (SEC-15): not a public gallery asset.
        result.Value.Url.Should().StartWith(Application.Ai.PharmacyPhotos.UrlPrefix).And.EndWith(".png");
        result.Value.Url.Should().NotStartWith(TemplateAssetUrls.UrlPrefix);
        store.Files.Single().Key.Should().StartWith("owner-1/");
        store.Files.Values.Single().Should().Equal(png);
    }

    [Fact]
    public async Task A_renamed_document_is_refused_and_nothing_is_stored()
    {
        var store = new MemoryUploads();
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 not a photo");

        var result = await Photos(store).UploadAsync("owner-1", new MemoryStream(pdf), pdf.Length, default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("JPEG, PNG or WebP");
        store.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task An_upload_over_12MB_is_refused()
    {
        var result = await Photos(new MemoryUploads()).UploadAsync(
            "owner-1", new MemoryStream(), Application.Ai.PosterPhotoService.MaxUploadBytes + 1, default);
        result.Error.Should().Contain("12MB");
    }

    [Fact]
    public void A_photo_failure_keeps_its_own_sentence()
    {
        AiTemplateGenerator.DescribeFailure(
            """{"detail":{"error":"AI images are not set up on this service. Choose stock, upload or no photo.","problems":[]}}""")
            .Should().StartWith("AI images are not set up");

        AiTemplateGenerator.DescribeFailure(
            """{"detail":{"error":"Could not produce a valid design in 3 attempts.","problems":["headline does not fit"]}}""")
            .Should().Contain("headline does not fit");
    }

    [Fact]
    public async Task A_pharmacy_upload_is_read_only_from_its_owners_area()
    {
        var uploads = new MemoryUploads();
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 7, 7, 7 };
        var stored = await Photos(uploads).UploadAsync("owner-1", new MemoryStream(jpeg), jpeg.Length, default);

        var handler = new Handler("""{"canvas":{"name":"x","shapes":[]},"name":"x","notes":[],"assets":[]}""");
        var generator = new AiTemplateGenerator(
            new HttpClient(handler) { BaseAddress = new Uri("http://ai-service") },
            Options.Create(new AiServiceOptions { BaseUrl = "http://ai-service" }),
            new MemoryAssets(), NullLogger<AiTemplateGenerator>.Instance, uploads);

        var mine = await generator.GenerateAsync(
            new GenerateTemplateRequest("flu clinic", Photo: new PosterPhoto(PosterPhotoMode.Upload, stored.Value!.Url),
                OwnerId: "owner-1"), "token", default);
        mine.IsSuccess.Should().BeTrue(mine.Error);
        Convert.FromBase64String(JsonNode.Parse(handler.Body!)!["photo"]!["data_base64"]!.GetValue<string>())
            .Should().Equal(jpeg);

        var theirs = await generator.GenerateAsync(
            new GenerateTemplateRequest("flu clinic", Photo: new PosterPhoto(PosterPhotoMode.Upload, stored.Value.Url),
                OwnerId: "owner-2"), "token", default);
        theirs.IsSuccess.Should().BeFalse("another pharmacy's upload must not be readable by naming it");
    }

    [Fact]
    public async Task Uploads_past_their_use_are_removed_when_the_pharmacy_uploads_again()
    {
        var uploads = new MemoryUploads();
        var old = Application.Ai.PharmacyPhotos.NewFileName(DateTime.UtcNow.AddDays(-10), "image/jpeg");
        await uploads.SaveAsync("owner-1", old, new MemoryStream(new byte[] { 1 }), "image/jpeg", default);
        var otherPharmacy = Application.Ai.PharmacyPhotos.NewFileName(DateTime.UtcNow.AddDays(-10), "image/jpeg");
        await uploads.SaveAsync("owner-2", otherPharmacy, new MemoryStream(new byte[] { 1 }), "image/jpeg", default);

        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 7 };
        await Photos(uploads).UploadAsync("owner-1", new MemoryStream(jpeg), jpeg.Length, default);

        uploads.Files.Keys.Should().NotContain($"owner-1/{old}", "a ten-day-old upload has served its purpose");
        uploads.Files.Keys.Should().Contain($"owner-2/{otherPharmacy}", "cleanup touches only the uploading pharmacy");
        uploads.Files.Keys.Count(k => k.StartsWith("owner-1/")).Should().Be(1);
    }

    [Theory]
    [InlineData("/api/posters/photos/20261003-0123456789abcdef0123456789abcdef.jpg", true)]
    [InlineData("https://app.example/api/posters/photos/20261003-0123456789abcdef0123456789abcdef.png", true)]
    [InlineData("/api/posters/photos/../../brand/logos/x.png", false)]
    [InlineData("/api/posters/photos/20261003-0123456789abcdef0123456789abcdef.svg", false)]
    [InlineData("/api/assets/templates/0123456789abcdef0123456789abcdef.jpg", false)]
    public void Only_well_formed_upload_names_are_recognised(string url, bool expected) =>
        Application.Ai.PharmacyPhotos.TryFileName(url, out _).Should().Be(expected);

    private sealed class MemoryUploads : Application.Ai.IPharmacyPhotoStore
    {
        public Dictionary<string, byte[]> Files { get; } = new();

        public async Task SaveAsync(string ownerId, string fileName, Stream content, string contentType, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            Files[$"{ownerId}/{fileName}"] = ms.ToArray();
        }

        public Task<Stream?> OpenAsync(string ownerId, string fileName, CancellationToken ct) =>
            Task.FromResult<Stream?>(Files.TryGetValue($"{ownerId}/{fileName}", out var b) ? new MemoryStream(b) : null);

        public Task<int> DeleteOlderThanAsync(string ownerId, DateTime cutoffUtc, CancellationToken ct)
        {
            var gone = Files.Keys
                .Where(k => k.StartsWith(ownerId + "/", StringComparison.Ordinal)
                            && Application.Ai.PharmacyPhotos.DateOf(k[(k.IndexOf('/') + 1)..]) is { } d && d < cutoffUtc.Date)
                .ToList();
            foreach (var k in gone) Files.Remove(k);
            return Task.FromResult(gone.Count);
        }
    }

    private static AiTemplateGenerator Generator(Handler handler, ITemplateAssetService assets) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://ai-service") },
            Options.Create(new AiServiceOptions { BaseUrl = "http://ai-service" }),
            assets, NullLogger<AiTemplateGenerator>.Instance);

    private sealed class Handler(string response) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class MemoryAssets : ITemplateAssetService
    {
        public Dictionary<string, byte[]> Files { get; } = new();

        public async Task<StoredTemplateAsset> UploadAsync(
            Stream content, string contentType, string fileExtension, CancellationToken ct)
        {
            var name = $"{Guid.NewGuid():N}.{TemplateAssetUrls.ExtensionByContentType[contentType]}";
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            Files[name] = buffer.ToArray();
            return new StoredTemplateAsset($"{TemplateAssetUrls.BlobPrefix}/{name}",
                TemplateAssetUrls.UrlFor(name), contentType);
        }

        public Task<Stream?> OpenAsync(string fileName, CancellationToken ct) =>
            Task.FromResult<Stream?>(Files.TryGetValue(fileName, out var b) ? new MemoryStream(b) : null);

        public string? TryResolveBlobKey(string urlOrPath) => TemplateAssetUrls.TryResolveBlobKey(urlOrPath);
        public bool IsAssetUrl(string urlOrPath) => TryResolveBlobKey(urlOrPath) is not null;
    }
}
