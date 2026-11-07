using System.Text;
using Acme.Application.Auth;
using Acme.Application.Posters;
using Acme.Domain.Common;
using Acme.Infrastructure.Export;
using Acme.Infrastructure.Imaging;
using Acme.Infrastructure.Security;
using Acme.Infrastructure.Storage;
using Acme.WebApi;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using Xunit;

namespace Acme.Tests.Security;

/// <summary>
/// The fixes from the 3 October 2026 security review
/// (docs/SECURITY_REVIEW_2026-10-03.pdf), one region per finding.
/// </summary>
public class SecurityReviewFixTests
{
    // ── SEC-01 / SEC-02: which image sources a poster may use ────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/api/brand-kit/logo")]
    [InlineData("/api/brand-kit/booking-qr")]
    [InlineData("/api/assets/templates/0123456789abcdef0123456789abcdef.png")]
    [InlineData("https://app.example.com/api/assets/templates/0123456789abcdef0123456789abcdef.jpg")]
    [InlineData("http://localhost:9090/api/brand-kit/logo")]
    [InlineData("https://images.unsplash.com/photo-1?w=800")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("data:image/jpeg;base64,/9j/")]
    public void Allowed_image_sources(string? src) =>
        CanvasImageSources.IsAllowed(src).Should().BeTrue();

    [Theory]
    [InlineData("http://169.254.169.254/metadata/instance")]
    [InlineData("http://10.0.0.5/admin")]
    [InlineData("https://evil.example.com/x.png")]
    [InlineData("http://images.unsplash.com/photo-1")]          // http, not https
    [InlineData("https://images.unsplash.com:8443/photo-1")]    // odd port
    [InlineData("file:///etc/passwd")]
    [InlineData("brand/logos/someone-else/logo.png")]            // raw storage key
    [InlineData("posters/abc/export.pdf")]
    [InlineData("/api/assets/templates/../../secrets.png")]
    [InlineData("/api/assets/templates/not-a-guid.png")]
    [InlineData("data:image/svg+xml;base64,PHN2Zz4=")]           // script-capable
    [InlineData("data:text/html;base64,PGgxPg==")]
    public void Refused_image_sources(string src) =>
        CanvasImageSources.IsAllowed(src).Should().BeFalse();

    [Fact]
    public void A_canvas_naming_an_internal_address_is_refused_and_blanked_at_render()
    {
        const string canvas =
            """{"shapes":[{"kind":"image","blobKey":"/api/brand-kit/logo"},{"kind":"image","blobKey":"http://169.254.169.254/latest"}]}""";

        CanvasImageSources.WhyRefused(canvas).Should().NotBeNull();

        var stripped = CanvasImageSources.StripDisallowed(canvas);
        stripped.Should().Contain("/api/brand-kit/logo").And.NotContain("169.254");
        CanvasImageSources.WhyRefused(stripped).Should().BeNull();
    }

    [Fact]
    public void A_json_escaped_source_is_judged_unescaped()
    {
        // "http:\/\/10.0.0.1\/" is the same URL to any JSON reader.
        const string canvas = """{"shapes":[{"blobKey":"http:\/\/10.0.0.1\/x.png"}]}""";
        CanvasImageSources.WhyRefused(canvas).Should().NotBeNull();
    }

    [Theory]
    [InlineData("https://images.unsplash.com/photo-1", true)]
    [InlineData("http://images.unsplash.com/photo-1", false)]
    [InlineData("https://images.unsplash.com.evil.com/x", false)]
    [InlineData("https://127.0.0.1/x", false)]
    public void The_renderer_fetches_only_allow_listed_https(string url, bool allowed) =>
        QuestPdfExporter.IsAllowedRemote(url).Should().Be(allowed);

    [Theory]
    [InlineData("brand/logos/user-1/abc.png", true)]
    [InlineData("brand/logos/../posters/x.pdf", false)]
    [InlineData("brand\\logos\\x.png", false)]
    [InlineData("templates/assets/x.png", false)]
    [InlineData("videos/x.mp4", false)]
    public void The_renderer_reads_only_brand_logo_keys_from_storage(string key, bool allowed) =>
        QuestPdfExporter.IsAllowedStorageKey(key).Should().Be(allowed);

    // ── SEC-04: the JWT secret ───────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short-secret")]
    [InlineData("replace-with-a-long-random-string-of-at-least-32-chars")]
    [InlineData("CHANGE_ME_CHANGE_ME_CHANGE_ME_CHANGE_ME")]
    [InlineData("your-secret-key-goes-here-make-it-long-enough")]
    public void Unsafe_jwt_secrets_are_named(string? secret) =>
        JwtSecretPolicy.WhyUnsafe(secret).Should().NotBeNull();

    [Fact]
    public void A_long_random_jwt_secret_is_accepted() =>
        JwtSecretPolicy.WhyUnsafe(Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray()))
            .Should().BeNull();

    // ── SEC-05: per-account sign-in lock ─────────────────────────────────────

    [Fact]
    public void An_account_locks_for_the_guessing_address_and_success_clears_it()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var throttle = new MemoryLoginThrottle(cache);

        for (var i = 0; i < ILoginThrottle.MaxFailuresPerAddress - 1; i++)
            throttle.RecordFailure("a@x.com", "198.51.100.1");
        throttle.IsLocked("a@x.com", "198.51.100.1").Should().BeFalse();

        throttle.RecordFailure(" A@X.com ", "198.51.100.1");   // same account, however it is typed
        throttle.IsLocked("a@x.com", "198.51.100.1").Should().BeTrue();
        throttle.IsLocked("b@x.com", "198.51.100.1").Should().BeFalse("one account's lock is not another's");

        throttle.RecordSuccess("a@x.com", "198.51.100.1");
        throttle.IsLocked("a@x.com", "198.51.100.1").Should().BeFalse();
    }

    // ── SEC-13: password policy ──────────────────────────────────────────────

    [Theory]
    [InlineData("short")]
    [InlineData("elevenchars")]
    [InlineData("Password2024!")]
    [InlineData("P@ssw0rd!!!!")]
    [InlineData("Pharmacy-2026!")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    public void Weak_passwords_are_refused(string password) =>
        PasswordPolicy.WhyRejected(password).Should().NotBeNull();

    [Fact]
    public void A_password_containing_the_email_name_is_refused() =>
        PasswordPolicy.WhyRejected("timothy-loves-cats", "timothy@pharmacy.test").Should().NotBeNull();

    [Fact]
    public void A_password_bcrypt_would_truncate_is_refused() =>
        PasswordPolicy.WhyRejected(new string('x', 40) + new string('y', 40)).Should().NotBeNull();

    [Theory]
    [InlineData("IntegrationTest-123!")]
    [InlineData("TestPass123!")]
    [InlineData("correct horse battery")]
    public void Ordinary_strong_passwords_are_accepted(string password) =>
        PasswordPolicy.WhyRejected(password, "someone@pharmacy.test").Should().BeNull();

    // ── SEC-10: SVG logos are stored as pixels ───────────────────────────────

    private const string RedSquareSvg =
        """<svg xmlns="http://www.w3.org/2000/svg" width="40" height="40"><rect width="40" height="40" fill="#f00"/></svg>""";

    [Theory]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><image href="http://169.254.169.254/x"/></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><image xlink:href='file:///etc/passwd'/></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><style>@import "http://x/y.css";</style></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><rect style="fill:url(http://x/p.svg#a)"/></svg>""")]
    [InlineData("""<!DOCTYPE svg [<!ENTITY x SYSTEM "file:///etc/passwd">]><svg>&x;</svg>""")]
    public void An_svg_that_points_outside_itself_is_not_rasterised(string svg)
    {
        SvgRasterizer.IsSelfContained(SvgRasterizer.WithoutDoctype(svg)).Should().BeFalse();
        SvgRasterizer.ToPng(Encoding.UTF8.GetBytes(svg)).Should().BeNull();
    }

    [Fact]
    public void A_self_contained_svg_with_a_plain_doctype_and_local_refs_is_rasterised()
    {
        const string svg =
            """<?xml version="1.0"?><!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">"""
            + """<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="40" height="40">"""
            + """<defs><linearGradient id="g"><stop offset="0" stop-color="#f00"/></linearGradient></defs>"""
            + """<rect width="40" height="40" fill="url(#g)"/></svg>""";

        var png = SvgRasterizer.ToPng(Encoding.UTF8.GetBytes(svg));
        png.Should().NotBeNull();
        SKBitmap.Decode(png!).Should().NotBeNull();
    }

    [Fact]
    public async Task An_svg_logo_upload_is_stored_as_png()
    {
        var blobs = new RecordingBlobs();
        var service = new AzureBrandAssetService(blobs);

        var key = await service.UploadLogoAsync(
            "user-1", new MemoryStream(Encoding.UTF8.GetBytes(RedSquareSvg)), "image/svg+xml", ".svg", default);

        key.Should().StartWith("brand/logos/user-1/").And.EndWith(".png");
        blobs.ContentType.Should().Be("image/png");
        blobs.Bytes.Should().NotBeNull();
        SKBitmap.Decode(blobs.Bytes!).Should().NotBeNull();
    }

    [Fact]
    public async Task A_logo_is_stored_by_what_its_bytes_are_not_what_it_claims()
    {
        var blobs = new RecordingBlobs();
        var service = new AzureBrandAssetService(blobs);
        using var bitmap = new SKBitmap(4, 4);
        using var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);

        var key = await service.UploadLogoAsync(
            "user-1", new MemoryStream(jpeg.ToArray()), "image/png", ".html", default);

        key.Should().EndWith(".jpg");
        blobs.ContentType.Should().Be("image/jpeg");
    }

    [Theory]
    [InlineData("<html><script>alert(1)</script></html>")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><image href="http://10.0.0.1/"/></svg>""")]
    public async Task Anything_else_is_refused_and_nothing_is_stored(string body)
    {
        var blobs = new RecordingBlobs();
        var service = new AzureBrandAssetService(blobs);

        var act = () => service.UploadLogoAsync(
            "user-1", new MemoryStream(Encoding.UTF8.GetBytes(body)), "image/png", ".png", default);

        await act.Should().ThrowAsync<DomainException>();
        blobs.Bytes.Should().BeNull();
    }

    private sealed class RecordingBlobs : IBlobStorageService
    {
        public byte[]? Bytes { get; private set; }
        public string? ContentType { get; private set; }

        public async Task<string> UploadAsync(string blobKey, Stream content, string contentType, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            Bytes = ms.ToArray();
            ContentType = contentType;
            return blobKey;
        }

        public Task<Stream> DownloadAsync(string blobKey, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string blobKey, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListKeysAsync(string prefix, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Uri GetReadOnlyUri(string blobKey, TimeSpan validFor) => new("https://example.invalid/" + blobKey);
    }
}
