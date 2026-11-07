using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// The pharmacy's photo choices for "Design with AI", through the real host:
/// a bad file is refused at upload, a foreign photo is refused before any
/// allowance is touched, and stock search is rate-limited per person.
/// </summary>
public sealed class PosterPhotoTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static MultipartFormDataContent File(byte[] bytes, string name, string contentType)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { part, "file", name } };
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_is_refused_at_upload_whatever_it_is_called()
    {
        RequireDatabase();
        var (client, _, _) = await Api.RegisterPharmacyAsync("photo-bad");

        using var res = await client.PostAsync("/api/posters/photos",
            File(Encoding.ASCII.GetBytes("%PDF-1.4 a document renamed to photo.png"), "photo.png", "image/png"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("isn't a JPEG, PNG or WebP");
    }

    [Fact]
    public async Task An_empty_upload_is_refused()
    {
        RequireDatabase();
        var (client, _, _) = await Api.RegisterPharmacyAsync("photo-empty");

        using var res = await client.PostAsync("/api/posters/photos", File([], "photo.jpg", "image/jpeg"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_photo_that_was_not_uploaded_here_is_refused_before_the_design_is_counted()
    {
        RequireDatabase();
        var (client, _, _) = await Api.RegisterPharmacyAsync("photo-foreign");

        using var res = await client.PostAsJsonAsync("/api/posters/generate", new
        {
            prompt = "trusted local pharmacy, circle portrait",
            photo = new { mode = "Upload", assetUrl = "https://elsewhere.example/someone.jpg" },
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("not uploaded here");

        // Refused before anything was spent: the month's count is untouched.
        var sub = await client.GetFromJsonAsync<JsonElement>("/api/subscription/current", Json);
        sub.GetProperty("usage").GetProperty("aiGenerationsThisMonth").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Stock_search_is_rate_limited_per_person()
    {
        RequireDatabase();
        var (client, _, _) = await Api.RegisterPharmacyAsync("photo-rate");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 21; i++)
        {
            using var res = await client.GetAsync("/api/posters/stock-photos?query=pharmacist");
            statuses.Add(res.StatusCode);
        }

        statuses.Take(20).Should().NotContain(HttpStatusCode.TooManyRequests);
        statuses[20].Should().Be(HttpStatusCode.TooManyRequests,
            "the stock key is shared by every pharmacy; one person's loop must stop at 20 a minute");

        // Somebody else is not affected.
        var (other, _, _) = await Api.RegisterPharmacyAsync("photo-rate-other");
        using var theirs = await other.GetAsync("/api/posters/stock-photos?query=pharmacist");
        theirs.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Photo_options_answer_for_any_signed_in_pharmacy()
    {
        RequireDatabase();
        var (client, _, _) = await Api.RegisterPharmacyAsync("photo-options");

        var options = await client.GetFromJsonAsync<JsonElement>("/api/posters/photo-options", Json);
        options.TryGetProperty("ai", out _).Should().BeTrue();
        options.TryGetProperty("stock", out _).Should().BeTrue();
    }
}
