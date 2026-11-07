using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// Stop, pressed while a campaign is going out.
/// </summary>
/// <remarks>
/// <para>
/// The one control a pharmacist has once a send is running, and the only one
/// that matters: the recipients are patients, the content is clinical
/// marketing, and there is no recall. It did not work.
/// </para>
/// <para>
/// The worker read the campaign once, before sending, and after every group of
/// twenty replaced the whole document with that copy — which still said
/// <c>Sending</c>. So Stop returned 200, the badge changed to Stopped, the next
/// group's write put it back to Sending, and the campaign ran to the last
/// recipient and finished as <c>Sent</c>. Nothing in the API or the UI showed
/// otherwise; the only evidence was in the inbox of every patient on the list.
/// </para>
/// <para>
/// The email sender is held for these tests rather than raced. Against the
/// mock, a forty-recipient campaign finishes in under a millisecond, so a test
/// that created one and then tried to stop it would have been testing how fast
/// its own HTTP calls were.
/// </para>
/// </remarks>
public sealed class CampaignStopTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Two groups of twenty, so there is a boundary to stop at.</summary>
    private const int Recipients = 40;

    private async Task<string> PublishedPosterAsync(HttpClient client, string label)
    {
        using var poster = await client.PostAsJsonAsync(
            "/api/posters", new { name = $"Poster for {label}" });
        poster.IsSuccessStatusCode.Should().BeTrue(
            "the campaign needs a poster: {0}", await poster.Content.ReadAsStringAsync());

        var id = (await poster.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("id").GetString()!;

        using var published = await client.PatchAsJsonAsync(
            $"/api/posters/{id}/status", new { status = "Published" });
        published.IsSuccessStatusCode.Should().BeTrue(
            "{0}", await published.Content.ReadAsStringAsync());

        return id;
    }

    private static async Task<JsonElement> CampaignAsync(HttpClient client, string id)
    {
        using var res = await client.GetAsync("/api/campaigns");
        res.IsSuccessStatusCode.Should().BeTrue();

        var all = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        foreach (var c in all.EnumerateArray())
            if (c.GetProperty("id").GetString() == id)
                return c.Clone();

        throw new InvalidOperationException($"Campaign {id} is not in the owner's list.");
    }

    private static async Task WaitForStatusAsync(HttpClient client, string id, string status)
    {
        // The worker sleeps three seconds between polls when idle, so a campaign
        // created just after it went to sleep waits that long to be claimed.
        var deadline = DateTime.UtcNow.AddSeconds(25);
        string? last = null;

        while (DateTime.UtcNow < deadline)
        {
            last = (await CampaignAsync(client, id)).GetProperty("status").GetString();
            if (last == status) return;
            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            $"Campaign {id} never reached '{status}' — it was '{last}' after 25s.");
    }

    /// <summary>
    /// Waits for the campaign to come to rest as stopped, having sent one group.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not <c>WaitForCampaignDispatchAsync</c>, which would return the instant
    /// Stop is written: a stopped campaign is no longer queued or sending, so
    /// the queue looks drained while the worker is still finishing the group it
    /// is holding. Every assertion after that would race it.
    /// </para>
    /// <para>
    /// The pair of conditions is also what makes this test unable to pass
    /// against the old behaviour. There, the group's write put the status back
    /// to <c>Sending</c> before the counter reached twenty, so "stopped, with
    /// twenty sent" was a state the campaign never occupied — it went to
    /// <c>Sent</c> with forty.
    /// </para>
    /// </remarks>
    private static async Task<JsonElement> WaitForStopAsync(
        HttpClient client, string id, int expectedSent)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        JsonElement last = default;

        while (DateTime.UtcNow < deadline)
        {
            last = await CampaignAsync(client, id);

            if (last.GetProperty("status").GetString() == "Stopped"
                && last.GetProperty("sentCount").GetInt32() == expectedSent)
            {
                return last;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            $"Campaign {id} never settled as Stopped with {expectedSent} sent. "
            + $"It is '{last.GetProperty("status").GetString()}' with "
            + $"{last.GetProperty("sentCount").GetInt32()} sent — which is what happens when the "
            + "worker never notices the stop and runs to the end of the list.");
    }

    [Fact]
    public async Task Stopping_a_running_campaign_stops_it()
    {
        RequireDatabase();

        var (client, _, userId) = await Api.RegisterPharmacyAsync("stop-running");
        await Api.UpgradeToProAsync(userId);
        var posterId = await PublishedPosterAsync(client, "stop-running");

        Api.Email.Clear();

        string campaignId;

        using (Api.Email.Hold())
        {
            using var created = await client.PostAsJsonAsync("/api/campaigns", new
            {
                posterId,
                name = "Stop me",
                channel = "Email",
                recipients = Enumerable.Range(0, Recipients)
                    .Select(i => $"stop-{i}@example.test")
                    .ToArray(),
            });
            created.IsSuccessStatusCode.Should().BeTrue(
                "{0}", await created.Content.ReadAsStringAsync());

            campaignId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json))
                .GetProperty("id").GetString()!;

            // The worker has claimed it and is inside the first group, waiting
            // on the held sender. This is the state the pharmacist is in when
            // they realise the wrong poster is going out.
            await WaitForStatusAsync(client, campaignId, "Sending");

            using var stop = await client.PostAsync($"/api/campaigns/{campaignId}/stop", null);
            stop.IsSuccessStatusCode.Should().BeTrue(
                "a running campaign is exactly what Stop is for: {0}",
                await stop.Content.ReadAsStringAsync());
        }

        // Released. The group already in flight lands — there is no recalling a
        // request the provider has accepted — and the worker then finds the stop
        // and leaves the rest alone.
        var after = await WaitForStopAsync(client, campaignId, expectedSent: 20);

        after.GetProperty("status").GetString().Should().Be("Stopped",
            "the pharmacist's decision is the one that stands. It used to finish as 'Sent', "
            + "which is worse than useless: it says the campaign they cancelled went out, and "
            + "it did");

        Api.Email.Sent.Should().HaveCount(1,
            "one group of 20 was in flight when Stop was pressed and could not be recalled. "
            + "A second message means the worker carried on past the boundary, which is what "
            + "it used to do — all {0} recipients, every time", Recipients);
    }

    [Fact]
    public async Task Recipients_reached_before_a_stop_are_not_sent_to_again_on_retry()
    {
        RequireDatabase();

        var (client, _, userId) = await Api.RegisterPharmacyAsync("stop-then-retry");
        await Api.UpgradeToProAsync(userId);
        var posterId = await PublishedPosterAsync(client, "stop-then-retry");

        Api.Email.Clear();

        string campaignId;

        using (Api.Email.Hold())
        {
            using var created = await client.PostAsJsonAsync("/api/campaigns", new
            {
                posterId,
                name = "Stop then retry",
                channel = "Email",
                recipients = Enumerable.Range(0, Recipients)
                    .Select(i => $"stop-retry-{i}@example.test")
                    .ToArray(),
            });
            created.IsSuccessStatusCode.Should().BeTrue(
                "{0}", await created.Content.ReadAsStringAsync());

            campaignId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json))
                .GetProperty("id").GetString()!;

            await WaitForStatusAsync(client, campaignId, "Sending");

            using var stop = await client.PostAsync($"/api/campaigns/{campaignId}/stop", null);
            stop.IsSuccessStatusCode.Should().BeTrue("{0}", await stop.Content.ReadAsStringAsync());
        }

        // A stopped campaign is not a failed one, and Retry refuses it. Sending
        // to the rest means creating a campaign for them — which is the point of
        // this test: the twenty who were reached must be visible as reached, so
        // that whoever picks this up knows who to exclude.
        var after = await WaitForStopAsync(client, campaignId, expectedSent: 20);
        var report = after.GetProperty("deliveryReport").EnumerateArray().ToList();

        report.Count(r => r.GetProperty("status").GetString() == "Sent")
            .Should().Be(20, "the first group is recorded, individually, by address");

        report.Count(r => r.GetProperty("status").GetString() == "Pending")
            .Should().Be(20, "the second group was never attempted and says so");
    }
}
