using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// Only a published poster goes out to patients.
/// </summary>
/// <remarks>
/// <para>
/// Draft, Published and Archived existed for a long time as three labels that
/// did nothing. The status was stored and it filtered the poster list; no other
/// part of the product read it. Publishing a poster visibly changed a badge and
/// changed nothing else, and a draft somebody was midway through editing could
/// be emailed to every patient on their list.
/// </para>
/// <para>
/// These tests are at the API boundary rather than against the service, because
/// the rule is only worth anything if it survives the round trip. A check that
/// lives in the browser is a suggestion.
/// </para>
/// </remarks>
public sealed class PosterPublishingRulesTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<(HttpClient Client, string PosterId)> PharmacyWithAPosterAsync(string label)
    {
        var (client, _, userId) = await Api.RegisterPharmacyAsync(label);
        await Api.UpgradeToProAsync(userId);

        using var poster = await client.PostAsJsonAsync(
            "/api/posters", new { name = $"Poster for {label}" });
        poster.IsSuccessStatusCode.Should().BeTrue(
            "the fixture needs a poster: {0}", await poster.Content.ReadAsStringAsync());

        var id = (await poster.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("id").GetString()!;

        return (client, id);
    }

    private static Task<HttpResponseMessage> SetStatusAsync(
        HttpClient client, string posterId, string status) =>
        client.PatchAsJsonAsync($"/api/posters/{posterId}/status", new { status });

    /// <summary>
    /// Asks for a campaign and comes back once its outcome is settled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every assertion in this class is about the mailbox — that a refused send
    /// reached nobody, that an accepted one reached somebody — and the POST no
    /// longer decides either. It queues, and a worker sends afterwards, so the
    /// helper has to close that gap or each test reads an empty box and draws
    /// the wrong conclusion from it. One of them, "the campaign went out", would
    /// have failed; the two "nothing may reach a patient" ones would have
    /// carried on passing while checking nothing.
    /// </para>
    /// <para>
    /// The wait runs before the POST as well. Cheap when the queue is empty, and
    /// it makes each test independent of what the one before it left behind.
    /// </para>
    /// </remarks>
    private async Task<HttpResponseMessage> SendCampaignAsync(
        HttpClient client, string posterId, string name)
    {
        await Api.WaitForCampaignDispatchAsync();
        Api.Email.Clear();

        var res = await client.PostAsJsonAsync("/api/campaigns", new
        {
            posterId,
            name,
            channel = "Email",
            recipients = new[] { "publishing-rules@example.test" },
        });

        // Accepted means queued, not sent: the worker sends a moment later.
        await Api.WaitForCampaignDispatchAsync();
        return res;
    }

    /* ── campaigns ───────────────────────────────────────────────────── */

    [Fact]
    public async Task A_draft_cannot_be_sent_in_a_campaign()
    {
        RequireDatabase();

        var (client, posterId) = await PharmacyWithAPosterAsync("draft-campaign");

        // Deliberately not publishing. A new poster is a draft, which is the
        // state a half-finished design is in.
        using var res = await SendCampaignAsync(client, posterId, "Draft campaign");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a draft is not something to send to patients");

        var body = await res.Content.ReadAsStringAsync();
        body.ToLowerInvariant().Should().Contain("draft",
            "the refusal has to say what is wrong");
        body.ToLowerInvariant().Should().Contain("publish",
            "and it has to say how to fix it");

        Api.Email.Sent.Should().BeEmpty(
            "nothing may reach a patient when the send was refused");
    }

    [Fact]
    public async Task An_archived_poster_cannot_be_sent_either()
    {
        RequireDatabase();

        var (client, posterId) = await PharmacyWithAPosterAsync("archived-campaign");
        (await SetStatusAsync(client, posterId, "Published")).IsSuccessStatusCode.Should().BeTrue();
        (await SetStatusAsync(client, posterId, "Archived")).IsSuccessStatusCode.Should().BeTrue();

        using var res = await SendCampaignAsync(client, posterId, "Archived campaign");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).ToLowerInvariant()
            .Should().Contain("archived");
        Api.Email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Publishing_is_what_makes_a_campaign_possible()
    {
        RequireDatabase();

        var (client, posterId) = await PharmacyWithAPosterAsync("publish-then-send");

        // The point of the whole change, in one test: the same request, refused
        // and then accepted, with publishing as the only thing that differs.
        using (var refused = await SendCampaignAsync(client, posterId, "Before publishing"))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        (await SetStatusAsync(client, posterId, "Published")).IsSuccessStatusCode
            .Should().BeTrue("publishing is a normal thing to do to your own poster");

        using var accepted = await SendCampaignAsync(client, posterId, "After publishing");

        accepted.IsSuccessStatusCode.Should().BeTrue(
            "publishing must actually enable the send: {0}",
            await accepted.Content.ReadAsStringAsync());

        Api.Email.Sent.Should().HaveCount(1, "the campaign went out");
    }

    [Fact]
    public async Task Unpublishing_after_a_send_leaves_the_sent_campaign_alone()
    {
        RequireDatabase();

        var (client, posterId) = await PharmacyWithAPosterAsync("unpublish-after");
        (await SetStatusAsync(client, posterId, "Published")).IsSuccessStatusCode.Should().BeTrue();

        using var sent = await SendCampaignAsync(client, posterId, "Already sent");
        sent.IsSuccessStatusCode.Should().BeTrue();
        Api.Email.Sent.Should().HaveCount(1);

        // Archive the poster afterwards. What already happened, happened —
        // rewriting history because a poster was later retired would be worse
        // than the problem it solved.
        (await SetStatusAsync(client, posterId, "Archived")).IsSuccessStatusCode.Should().BeTrue();

        using var list = await client.GetAsync("/api/campaigns");
        list.IsSuccessStatusCode.Should().BeTrue();
        (await list.Content.ReadAsStringAsync()).Should().Contain("Already sent",
            "the campaign that was sent stays in the pharmacy's history");
    }

    /* ── social ──────────────────────────────────────────────────────── */

    [Fact]
    public async Task A_draft_cannot_be_posted_to_social_either()
    {
        RequireDatabase();

        var (client, posterId) = await PharmacyWithAPosterAsync("draft-social");

        using var res = await client.PostAsJsonAsync("/api/social/posts", new
        {
            sourceKind = "Poster",
            sourceId = posterId,
            caption = "Come and see us",
            connectionIds = new[] { "no-such-connection" },
        });

        // The connection id is deliberately bogus, so this test can only pass
        // for the right reason: if the poster check did not run first, the
        // failure would be about the connection instead.
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).ToLowerInvariant()
            .Should().Contain("publish",
                "the poster is checked before the accounts are, so a draft is refused as a draft");
    }
}
