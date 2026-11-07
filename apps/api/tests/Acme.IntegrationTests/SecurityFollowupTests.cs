using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acme.Application.Messaging;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// The follow-up review's fixes, end to end through the HTTP pipeline
/// (docs/SECURITY_REVIEW_2026-10-03_FOLLOWUP.pdf). Rules are unit-tested in
/// <c>Acme.Tests/Security/SecurityFollowupFixTests</c>.
/// </summary>
public sealed class SecurityFollowupTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── R2-05: a differently-cased source key is still judged ───────────────

    [Fact]
    public async Task A_poster_cannot_smuggle_an_image_source_under_another_spelling()
    {
        RequireDatabase();
        var (client, _, _) = await Api.RegisterPharmacyAsync("case-key");
        var canvas = JsonDocument.Parse(
            """{"width":1240,"height":1754,"background":"#FFFFFF","shapes":[{"id":"i","kind":"image","position":{"x":0,"y":0},"width":10,"height":10,"zIndex":1,"BlobKey":"http://169.254.169.254/x"}]}""")
            .RootElement;

        using var res = await client.PostAsJsonAsync("/api/posters", new { name = "Sneaky", canvas });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
    }

    // ── R2-09: deactivation ends the session at once ────────────────────────

    [Fact]
    public async Task A_deactivated_account_is_refused_on_its_next_request()
    {
        RequireDatabase();
        var (pharmacy, _, id) = await Api.RegisterPharmacyAsync("revoked");
        (await pharmacy.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.OK,
            "the token works before deactivation (and its state is now cached)");

        var admin = await Api.SignInSuperAdminAsync();
        using var deactivate = await admin.PostAsJsonAsync($"/api/admin/users/{id}/deactivate", new { });
        deactivate.IsSuccessStatusCode.Should().BeTrue(await deactivate.Content.ReadAsStringAsync());

        using var after = await pharmacy.GetAsync("/api/me");
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an access token issued before deactivation must stop working, not run to its expiry");
    }

    // ── R2-06: unsubscribe ───────────────────────────────────────────────────

    private async Task<(HttpClient Client, string PosterId)> SenderAsync(string label)
    {
        var (client, _, userId) = await Api.RegisterPharmacyAsync(label);
        await Api.UpgradeToProAsync(userId);

        using var poster = await client.PostAsJsonAsync("/api/posters", new { name = $"Poster {label}" });
        var posterId = (await poster.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetString()!;
        using var published = await client.PatchAsJsonAsync($"/api/posters/{posterId}/status", new { status = "Published" });
        published.IsSuccessStatusCode.Should().BeTrue(await published.Content.ReadAsStringAsync());
        return (client, posterId);
    }

    private static Task<HttpResponseMessage> CampaignAsync(HttpClient client, string posterId, params string[] to) =>
        client.PostAsJsonAsync("/api/campaigns", new
        {
            posterId,
            name = $"Campaign {Guid.NewGuid():N}",
            channel = "Email",
            recipients = to,
        });

    private static string TokenFrom(string url) =>
        Uri.UnescapeDataString(url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..]);

    [Fact]
    public async Task Every_campaign_email_carries_its_recipients_own_way_out()
    {
        RequireDatabase();
        var (client, posterId) = await SenderAsync("unsub-links");
        Api.Email.Clear();

        using (var res = await CampaignAsync(client, posterId, "unsub-a@example.test", "unsub-b@example.test"))
            res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync());
        await Api.WaitForCampaignDispatchAsync();

        var mail = Api.Email.Last;
        mail.HtmlBody.Should().Contain(UnsubscribeLinks.Placeholder, "the sender fills in each person's own link");
        mail.Unsubscribe.Should().NotBeNull();
        mail.Unsubscribe!.Keys.Should().BeEquivalentTo("unsub-a@example.test", "unsub-b@example.test");
        mail.Unsubscribe["unsub-a@example.test"].PageUrl.Should().Contain("/unsubscribe?token=");
        mail.Unsubscribe["unsub-a@example.test"].OneClickUrl.Should().Contain("/api/unsubscribe?token=");
        TokenFrom(mail.Unsubscribe["unsub-a@example.test"].PageUrl)
            .Should().NotBe(TokenFrom(mail.Unsubscribe["unsub-b@example.test"].PageUrl));
    }

    [Fact]
    public async Task An_unsubscribed_address_is_left_out_of_every_later_campaign()
    {
        RequireDatabase();
        var (client, posterId) = await SenderAsync("unsub-flow");
        Api.Email.Clear();

        using (var res = await CampaignAsync(client, posterId, "leaver@example.test", "stayer@example.test"))
            res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync());
        await Api.WaitForCampaignDispatchAsync();
        var token = TokenFrom(Api.Email.Last.Unsubscribe!["leaver@example.test"].OneClickUrl);

        // Anonymous, as the patient is. GET only describes.
        var anon = Api.NewClient();
        var url = $"/api/unsubscribe?token={Uri.EscapeDataString(token)}";
        var before = await anon.GetFromJsonAsync<JsonElement>(url, Json);
        before.GetProperty("unsubscribed").GetBoolean().Should().BeFalse("opening the link must not unsubscribe");
        before.GetProperty("maskedEmail").GetString().Should().NotContain("leaver@");

        // What a mail client's one-click button sends (RFC 8058).
        using (var oneClick = await anon.PostAsync(url,
                   new FormUrlEncodedContent(new Dictionary<string, string> { ["List-Unsubscribe"] = "One-Click" })))
            oneClick.StatusCode.Should().Be(HttpStatusCode.OK, await oneClick.Content.ReadAsStringAsync());

        (await anon.GetFromJsonAsync<JsonElement>(url, Json)).GetProperty("unsubscribed").GetBoolean().Should().BeTrue();

        // A campaign to only them is refused; a mixed one leaves them out.
        using (var onlyThem = await CampaignAsync(client, posterId, "leaver@example.test"))
            onlyThem.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        Api.Email.Clear();
        using (var mixed = await CampaignAsync(client, posterId, "LEAVER@example.test", "stayer@example.test"))
            mixed.IsSuccessStatusCode.Should().BeTrue(await mixed.Content.ReadAsStringAsync());
        await Api.WaitForCampaignDispatchAsync();

        Api.Email.Sent.SelectMany(m => m.To).Should().NotContain(t => t.Equals("leaver@example.test", StringComparison.OrdinalIgnoreCase));
        Api.Email.Sent.SelectMany(m => m.To).Should().Contain("stayer@example.test");
    }

    [Fact]
    public async Task A_forged_unsubscribe_link_is_refused()
    {
        RequireDatabase();
        using var res = await Api.NewClient().PostAsync("/api/unsubscribe?token=b3duZXJ8eEB5LmNvbQ.AAAA", null);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── SEC-15: a pharmacy's photo uploads are private ──────────────────────

    [Fact]
    public async Task Photo_uploads_are_served_only_to_their_own_pharmacy()
    {
        RequireDatabase();
        const string someone = "/api/posters/photos/20261003-0123456789abcdef0123456789abcdef.jpg";

        // Signed out: the route is not public, unlike template assets.
        using (var anon = await Api.NewClient().GetAsync(someone))
            anon.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Signed in: only the caller's own area is searched, so a name that is
        // not theirs is simply not found, and a malformed one never reaches storage.
        var (client, _, _) = await Api.RegisterPharmacyAsync("photo-private");
        using (var notMine = await client.GetAsync(someone))
            notMine.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var traversal = await client.GetAsync("/api/posters/photos/..%2F..%2Fbrand%2Flogos%2Fx.png"))
            traversal.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Owner scoping when a design reads an upload is proven in the unit suite
    // (AiDesignPhotoTests.A_pharmacy_upload_is_read_only_from_its_owners_area):
    // here, generation fails for unrelated reasons (no AI service, plan limits).
}
