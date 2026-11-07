using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// <c>GET /api/posters/{id}/card</c>: the My Posters card, end to end.
/// </summary>
/// <remarks>
/// The list used to hand the browser a signed storage URL for whatever had
/// last been rendered for publishing. Published posters showed a broken "?";
/// the rest fell back to a browser-drawn preview. These pin the replacement:
/// a same-origin image from the PDF renderer, revalidated by ETag, owner only.
/// </remarks>
public sealed class PosterCardTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A poster whose canvas is the Advice checklist house design.
    /// </summary>
    /// <remarks>
    /// Posted as a canvas rather than created from the seeded template: the
    /// test host has no blob storage, and the seeder holds back any house
    /// template whose photograph it cannot upload — so the template may not
    /// exist here. The canvas is read from the same embedded file the seeder
    /// uses. Its photograph then cannot be fetched either, which is the
    /// "missing image" case the card must survive.
    /// </remarks>
    private async Task<(HttpClient Client, string PosterId, string CardUrl)> AdviceChecklistPosterAsync()
    {
        var (client, _, _) = await Api.RegisterPharmacyAsync("card");

        using var created = await client.PostAsJsonAsync("/api/posters",
            new { name = "Advice checklist", canvas = AdviceChecklistCanvas() });
        created.IsSuccessStatusCode.Should().BeTrue(await created.Content.ReadAsStringAsync());

        var poster = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        return (client, poster.GetProperty("id").GetString()!, poster.GetProperty("thumbnailUrl").GetString()!);
    }

    private static JsonObject AdviceChecklistCanvas()
    {
        using var stream = typeof(Acme.Infrastructure.Persistence.TemplateSeeder).Assembly
            .GetManifestResourceStream("HouseTemplates.templates.json")!;
        var template = JsonNode.Parse(stream)!.AsArray()
            .Single(t => t!["id"]!.GetValue<string>() == "sys-house-advice-checklist")!;

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = "advice-checklist",
            ["ownerId"] = "test",
            ["name"] = "Advice checklist",
            ["width"] = 1240,
            ["height"] = 1754,
            ["background"] = template["background"]!.GetValue<string>(),
            ["viewport"] = new JsonObject
            {
                ["pan"] = new JsonObject { ["x"] = 0, ["y"] = 0 },
                ["zoom"] = 1,
            },
            ["shapes"] = template["shapes"]!.DeepClone(),
        };
    }

    [Fact]
    public async Task The_card_is_a_same_origin_jpeg_and_never_empty()
    {
        RequireDatabase();
        var (client, id, cardUrl) = await AdviceChecklistPosterAsync();

        cardUrl.Should().StartWith($"/api/posters/{id}/card",
            "the browser must never be sent to storage directly");

        using var res = await client.GetAsync(cardUrl);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        res.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        res.Headers.ETag.Should().NotBeNull();
        res.Headers.CacheControl!.NoCache.Should().BeTrue();

        var bytes = await res.Content.ReadAsByteArrayAsync();
        bytes.Should().NotBeEmpty();
        using var card = SKBitmap.Decode(bytes);
        card.Should().NotBeNull();
        card!.Width.Should().Be(600);
    }

    [Fact]
    public async Task An_unchanged_card_is_a_304_and_an_edit_changes_it()
    {
        RequireDatabase();
        var (client, id, cardUrl) = await AdviceChecklistPosterAsync();

        using var first = await client.GetAsync(cardUrl);
        var etag = first.Headers.ETag!;

        using var again = new HttpRequestMessage(HttpMethod.Get, cardUrl);
        again.Headers.IfNoneMatch.Add(etag);
        using var notModified = await client.SendAsync(again);
        notModified.StatusCode.Should().Be(HttpStatusCode.NotModified);

        // Edit the poster: the card must change with it, not be served stale.
        var poster = await client.GetFromJsonAsync<JsonElement>($"/api/posters/{id}", Json);
        var canvas = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(poster.GetProperty("canvas").GetRawText())!;
        canvas["background"] = JsonSerializer.SerializeToElement("#ffffff");
        using var saved = await client.PutAsJsonAsync($"/api/posters/{id}", new { name = "Advice checklist", canvas });
        saved.IsSuccessStatusCode.Should().BeTrue(await saved.Content.ReadAsStringAsync());

        using var afterEdit = new HttpRequestMessage(HttpMethod.Get, $"/api/posters/{id}/card");
        afterEdit.Headers.IfNoneMatch.Add(etag);
        using var changed = await client.SendAsync(afterEdit);
        changed.StatusCode.Should().Be(HttpStatusCode.OK, "an edited poster gets a new card");
        changed.Headers.ETag!.Tag.Should().NotBe(etag.Tag);
    }

    [Fact]
    public async Task Viewing_a_card_does_not_reorder_the_list()
    {
        RequireDatabase();
        var (client, id, cardUrl) = await AdviceChecklistPosterAsync();
        var before = (await client.GetFromJsonAsync<JsonElement>($"/api/posters/{id}", Json))
            .GetProperty("updatedAtUtc").GetString();

        using var viewed = await client.GetAsync(cardUrl);
        viewed.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = (await client.GetFromJsonAsync<JsonElement>($"/api/posters/{id}", Json))
            .GetProperty("updatedAtUtc").GetString();
        after.Should().Be(before, "My Posters is ordered by last update; looking is not updating");
    }

    [Fact]
    public async Task Another_pharmacy_cannot_see_the_card()
    {
        RequireDatabase();
        var (_, _, cardUrl) = await AdviceChecklistPosterAsync();
        var (stranger, _, _) = await Api.RegisterPharmacyAsync("card-stranger");

        using var res = await stranger.GetAsync(cardUrl);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
