using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// The monthly recipient allowance, counted while campaigns are still going out.
/// </summary>
/// <remarks>
/// <para>
/// The limit is what separates the plans, so it has to hold at the moment a
/// pharmacy is most likely to cross it: several campaigns created one after
/// another. It stopped holding when dispatch moved to a background worker, and
/// nothing failed — the limit had no test at all, and the change that broke it
/// touched neither the plan code nor the counting code.
/// </para>
/// <para>
/// Sending used to finish inside the request that created the campaign, so
/// <c>SentAtUtc</c> and <c>SentCount</c> were written before the response came
/// back and the next campaign's check saw them. Queued campaigns have neither
/// until a worker finishes, so every check read zero and a pharmacy on 500 a
/// month could send several thousand by clicking quickly.
/// </para>
/// </remarks>
public sealed class CampaignPlanLimitTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Starter allows 500 recipients a month.</summary>
    private const int StarterAllowance = 500;

    private static async Task<string> PublishedPosterAsync(HttpClient client, string label)
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
            "the poster must be published before it can be sent: {0}",
            await published.Content.ReadAsStringAsync());

        return id;
    }

    private static Task<HttpResponseMessage> CreateCampaignAsync(
        HttpClient client, string posterId, string name, int recipientCount) =>
        client.PostAsJsonAsync("/api/campaigns", new
        {
            posterId,
            name,
            channel = "Email",
            recipients = Enumerable
                .Range(0, recipientCount)
                .Select(i => $"{name.ToLowerInvariant().Replace(' ', '-')}-{i}@example.test")
                .ToArray(),
        });

    [Fact]
    public async Task A_campaign_still_in_the_queue_counts_against_the_monthly_allowance()
    {
        RequireDatabase();

        var (client, _, userId) = await Api.RegisterPharmacyAsync("plan-limit");
        await Api.SetTierAsync(userId, "Starter");

        var posterId = await PublishedPosterAsync(client, "plan-limit");

        // Held for the whole of the interesting part. Without this the worker
        // finishes the first campaign in milliseconds, the old counting works by
        // accident, and the test passes against the bug it exists to catch.
        using (Api.Email.Hold())
        {
            using var first = await CreateCampaignAsync(client, posterId, "Limit first", 400);
            first.IsSuccessStatusCode.Should().BeTrue(
                "400 is within the {0} allowance: {1}",
                StarterAllowance, await first.Content.ReadAsStringAsync());

            // 400 committed, so only 100 remain. The second campaign has to be
            // refused on what the pharmacy has already asked for, not on what
            // has so far come out the other end.
            using var second = await CreateCampaignAsync(client, posterId, "Limit second", 200);

            second.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "400 queued plus 200 more is over the {0} allowance. Accepting this is how a "
                + "Starter pharmacy sends 2,500 in a minute: create, create, create — none of "
                + "them finished, so none of them counted", StarterAllowance);

            (await second.Content.ReadAsStringAsync()).Should().Contain(
                StarterAllowance.ToString(),
                "the refusal has to name the limit it is enforcing");
        }

        // Released: the first campaign goes out, and the allowance now reads the
        // same 400 from the other side of the send.
        await Api.WaitForCampaignDispatchAsync();

        using var third = await CreateCampaignAsync(client, posterId, "Limit third", 200);
        third.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the 400 that have now actually been sent count for exactly as much as they did "
            + "while they were queued — the pharmacy is not charged twice, and not let off either");
    }

    [Fact]
    public async Task What_a_queued_campaign_reserves_is_released_if_it_never_sends()
    {
        RequireDatabase();

        var (client, _, userId) = await Api.RegisterPharmacyAsync("plan-limit-stopped");
        await Api.SetTierAsync(userId, "Starter");

        var posterId = await PublishedPosterAsync(client, "plan-limit-stopped");

        string campaignId;

        using (Api.Email.Hold())
        {
            using var first = await CreateCampaignAsync(client, posterId, "Reserved", 400);
            first.IsSuccessStatusCode.Should().BeTrue(
                "{0}", await first.Content.ReadAsStringAsync());

            campaignId = (await first.Content.ReadFromJsonAsync<JsonElement>(Json))
                .GetProperty("id").GetString()!;

            using var stop = await client.PostAsync($"/api/campaigns/{campaignId}/stop", null);
            stop.IsSuccessStatusCode.Should().BeTrue(
                "a queued campaign can be stopped: {0}", await stop.Content.ReadAsStringAsync());
        }

        await Api.WaitForCampaignDispatchAsync();

        // A stopped campaign is neither queued nor sent, so it reserves nothing.
        // Counting it would leave the pharmacy paying, for the rest of the
        // month, for a send it cancelled.
        using var second = await CreateCampaignAsync(client, posterId, "After stop", 400);
        second.IsSuccessStatusCode.Should().BeTrue(
            "a stopped campaign must not go on consuming the allowance: {0}",
            await second.Content.ReadAsStringAsync());
    }
}
