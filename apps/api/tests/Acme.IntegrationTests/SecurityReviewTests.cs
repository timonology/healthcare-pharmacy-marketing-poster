using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// The 3 October 2026 security review's fixes, end to end through the HTTP
/// pipeline (docs/SECURITY_REVIEW_2026-10-03.pdf). The rules themselves are
/// unit-tested in <c>Acme.Tests/Security/SecurityReviewFixTests</c>; these
/// prove they are wired to the endpoints that need them.
/// </summary>
public sealed class SecurityReviewTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string CanvasWithImage(string src) => $$"""
        {"width":1240,"height":1754,"background":"#FFFFFF","shapes":[
          {"id":"img","kind":"image","position":{"x":0,"y":0},"width":100,"height":100,"zIndex":1,"blobKey":"{{src}}"}
        ]}
        """;

    // ── SEC-01 / SEC-02 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://169.254.169.254/metadata/instance")]
    [InlineData("brand/logos/another-pharmacy/logo.png")]
    public async Task A_poster_naming_an_unsafe_image_source_cannot_be_created_or_saved(string src)
    {
        RequireDatabase();
        var (client, _, _) = await Api.RegisterPharmacyAsync("ssrf");
        var canvas = JsonDocument.Parse(CanvasWithImage(src)).RootElement;

        using var create = await client.PostAsJsonAsync("/api/posters", new { name = "Unsafe", canvas });
        create.StatusCode.Should().Be(HttpStatusCode.BadRequest, await create.Content.ReadAsStringAsync());

        using var ok = await client.PostAsJsonAsync("/api/posters", new { name = "Safe" });
        ok.IsSuccessStatusCode.Should().BeTrue(await ok.Content.ReadAsStringAsync());
        var id = (await ok.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetString();

        using var save = await client.PutAsJsonAsync($"/api/posters/{id}", new { name = "Safe", canvas });
        save.StatusCode.Should().Be(HttpStatusCode.BadRequest, await save.Content.ReadAsStringAsync());
        (await save.Content.ReadAsStringAsync()).Should().Contain("Upload the picture");
    }

    // ── SEC-05: per-account sign-in lock ─────────────────────────────────────

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account_even_for_the_right_one()
    {
        RequireDatabase();
        var (_, email, _) = await Api.RegisterPharmacyAsync("throttle");
        var client = Api.NewClient();

        for (var i = 0; i < 8; i++)
        {
            using var bad = await client.PostAsJsonAsync("/api/auth/login", new { email, password = $"wrong-password-{i}" });
            bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using var right = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "IntegrationTest-123!" });
        right.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await right.Content.ReadAsStringAsync()).Should().Contain("Too many sign-in attempts");
    }

    // ── SEC-06: share-email caps ─────────────────────────────────────────────

    [Fact]
    public async Task Sharing_a_poster_is_capped_in_recipients_and_refuses_links()
    {
        RequireDatabase();
        var (client, _, _) = await Api.RegisterPharmacyAsync("share-caps");
        using var created = await client.PostAsJsonAsync("/api/posters", new { name = "Caps" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetString();
        Api.Email.Clear();

        var eleven = Enumerable.Range(1, 11).Select(i => $"r{i}@example.test").ToArray();
        using var tooMany = await client.PostAsJsonAsync($"/api/posters/{id}/share-email",
            new { recipients = eleven, subject = (string?)null, message = "Hi" });
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var link = await client.PostAsJsonAsync($"/api/posters/{id}/share-email",
            new { recipients = new[] { "r@example.test" }, subject = "Offer", message = "Claim it at http://phish.example" });
        link.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        Api.Email.Sent.Should().BeEmpty("a refused share sends nothing");
    }

    // ── SEC-09: response headers ─────────────────────────────────────────────

    [Fact]
    public async Task Api_responses_carry_the_security_headers()
    {
        RequireDatabase();
        using var res = await Api.NewClient().GetAsync("/health");

        res.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        res.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        res.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("default-src 'none'");
    }

    // ── SEC-10: logo uploads ─────────────────────────────────────────────────

    private async Task<HttpClient> PharmacyWithKitAsync(string label)
    {
        var (client, _, _) = await Api.RegisterPharmacyAsync(label);
        using var kit = await client.PutAsJsonAsync("/api/brand-kit", new
        {
            name = "Kit",
            colors = new { primary = "#0F766E", secondary = "#134E4A", accent = "#F59E0B" },
            pharmacy = new
            {
                name = "Security Test Pharmacy",
                licenseNumber = "FJ123",
                phone = "0161 000 0000",
                address = "1 Test Street, Manchester",
            },
            regulatoryFooter = "",
        });
        kit.IsSuccessStatusCode.Should().BeTrue(await kit.Content.ReadAsStringAsync());
        return client;
    }

    private static MultipartFormDataContent FileForm(string body, string contentType, string name)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", name } };
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_is_refused_as_a_logo()
    {
        RequireDatabase();
        var client = await PharmacyWithKitAsync("logo-html");

        using var res = await client.PostAsync("/api/brand-kit/logo",
            FileForm("<html><script>alert(document.cookie)</script></html>", "image/png", "logo.png"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
    }

    // SVG to PNG at upload needs blob storage to round-trip, so it is proven
    // in the unit tests (SecurityReviewFixTests) against a recording store.
}
