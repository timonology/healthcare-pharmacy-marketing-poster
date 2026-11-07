using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Acme.Application.Social;
using Acme.Domain.Social;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// The parts of recurring posts that only the database can prove: two
/// schedulers make one occurrence, two workers claim it once, and the plan
/// limit and pause hold through the real endpoints.
/// </summary>
/// <remarks>
/// The background publish and series workers are off in this host (see
/// <see cref="ApiFactory"/>), so each step here runs when the test says, with
/// the clock it passes.
/// </remarks>
public sealed class RecurringSeriesConcurrencyTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IMongoDatabase Db => new MongoClient(Api.MongoUrl).GetDatabase(Api.DatabaseName);

    private async Task<(HttpClient Client, string Id, string PosterId, string ConnectionId)> PharmacyAsync(string label, string tier = "Pro")
    {
        var (client, _, id) = await Api.RegisterPharmacyAsync(label);
        await Api.SetTierAsync(id, tier);

        using var poster = await client.PostAsJsonAsync("/api/posters", new { name = $"Series poster {label}" });
        poster.IsSuccessStatusCode.Should().BeTrue(await poster.Content.ReadAsStringAsync());
        var posterId = (await poster.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetString()!;
        using var published = await client.PatchAsJsonAsync($"/api/posters/{posterId}/status", new { status = "Published" });
        published.IsSuccessStatusCode.Should().BeTrue(await published.Content.ReadAsStringAsync());

        // A connected Facebook Page, written directly: connecting one for real
        // needs Meta's OAuth.
        // Nothing here is ever sent, so the token is a placeholder rather than
        // something the host's cipher (which may not be configured) produced.
        using var scope = Api.Services.CreateScope();
        var connection = SocialConnection.Create(
            id, SocialPlatform.FacebookPage, $"page-{Guid.NewGuid():N}", "Test Page",
            EncryptedSecret.Create("not-a-real-token", "nonce", "tag", 1), null, null);
        await scope.ServiceProvider.GetRequiredService<ISocialConnectionRepository>().AddAsync(connection, default);

        return (client, id, posterId, connection.Id);
    }

    private static object Body(string posterId, string connectionId) => new
    {
        name = "Flu clinic, twice a week",
        sourceKind = "Poster",
        sourceId = posterId,
        caption = "Walk in, no appointment needed.",
        connectionIds = new[] { connectionId },
        cadence = new { kind = "Weekly", time = "09:00", days = new[] { "Monday", "Thursday" } },
        timeZoneId = "Europe/London",
        startDate = DateTime.UtcNow.ToString("yyyy-MM-dd"),
    };

    private async Task<string> CreateSeriesAsync(HttpClient client, string posterId, string connectionId)
    {
        using var res = await client.PostAsJsonAsync("/api/social/series", Body(posterId, connectionId));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetString()!;
    }

    private Task<long> OccurrenceCountAsync(string seriesId) =>
        Db.GetCollection<BsonDocument>("scheduled_posts")
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("seriesId", seriesId));

    [Fact]
    public async Task Two_schedulers_make_one_occurrence_and_two_workers_claim_it_once()
    {
        RequireDatabase();
        var (client, _, posterId, connectionId) = await PharmacyAsync("series-race");
        var seriesId = await CreateSeriesAsync(client, posterId, connectionId);

        // Two scheduler instances, each with its own scope and its own copy of
        // the series, processing the same due occurrence at the same moment.
        using var scopeA = Api.Services.CreateScope();
        using var scopeB = Api.Services.CreateScope();
        var copyA = await scopeA.ServiceProvider.GetRequiredService<IRecurringSeriesRepository>().FindByIdAsync(seriesId, default);
        var copyB = await scopeB.ServiceProvider.GetRequiredService<IRecurringSeriesRepository>().FindByIdAsync(seriesId, default);
        var due = copyA!.NextOccurrenceUtc!.Value;
        var now = due.AddMinutes(1);

        await Task.WhenAll(
            scopeA.ServiceProvider.GetRequiredService<RecurringSeriesService>().ProcessDueAsync(copyA, now, default),
            scopeB.ServiceProvider.GetRequiredService<RecurringSeriesService>().ProcessDueAsync(copyB!, now, default));

        (await OccurrenceCountAsync(seriesId)).Should().Be(1, "the occurrence id is the series and the instant");

        // Two publish workers race to claim everything due. The occurrence
        // must end up with exactly one of them.
        var postsA = scopeA.ServiceProvider.GetRequiredService<IScheduledPostRepository>();
        var postsB = scopeB.ServiceProvider.GetRequiredService<IScheduledPostRepository>();

        async Task<List<string>> Drain(IScheduledPostRepository posts, string holder)
        {
            var claimed = new List<string>();
            while (await posts.ClaimNextDueAsync(holder, TimeSpan.FromMinutes(5), now, default) is { } p)
                claimed.Add(p.Id);
            return claimed;
        }

        var results = await Task.WhenAll(Drain(postsA, "worker-a"), Drain(postsB, "worker-b"));
        var occurrenceId = copyA.OccurrenceId(due);

        results.Count(r => r.Contains(occurrenceId)).Should().Be(1, "only one worker may claim an occurrence");
        results.Sum(r => r.Count(id => id == occurrenceId)).Should().Be(1);

        // And the series moved on exactly once.
        var after = await scopeA.ServiceProvider.GetRequiredService<IRecurringSeriesRepository>().FindByIdAsync(seriesId, default);
        after!.NextOccurrenceUtc.Should().BeAfter(due);
        after.Status.Should().Be(SeriesStatus.Active);
    }

    [Fact]
    public async Task Pausing_through_the_api_cancels_what_is_queued_and_keeps_what_was_sent()
    {
        RequireDatabase();
        var (client, _, posterId, connectionId) = await PharmacyAsync("series-pause");
        var seriesId = await CreateSeriesAsync(client, posterId, connectionId);

        using var scope = Api.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecurringSeriesRepository>();
        var service = scope.ServiceProvider.GetRequiredService<RecurringSeriesService>();
        var posts = Db.GetCollection<BsonDocument>("scheduled_posts");

        // First occurrence: queued, then marked as sent.
        var s1 = await repo.FindByIdAsync(seriesId, default);
        var first = s1!.NextOccurrenceUtc!.Value;
        await service.ProcessDueAsync(s1, first.AddMinutes(1), default);
        var sentId = s1.OccurrenceId(first);
        await posts.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", sentId),
            Builders<BsonDocument>.Update.Set("status", (int)PublishStatus.Published).Set("completedAtUtc", DateTime.UtcNow));

        // Second occurrence: queued, not started.
        var s2 = await repo.FindByIdAsync(seriesId, default);
        var second = s2!.NextOccurrenceUtc!.Value;
        await service.ProcessDueAsync(s2, second.AddMinutes(1), default);
        var queuedId = s2.OccurrenceId(second);

        using var pause = await client.PostAsync($"/api/social/series/{seriesId}/pause", null);
        pause.StatusCode.Should().Be(HttpStatusCode.OK, await pause.Content.ReadAsStringAsync());

        var status = async (string id) => (await posts.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstAsync())["status"].AsInt32;
        (await status(sentId)).Should().Be((int)PublishStatus.Published, "pausing leaves history alone");
        (await status(queuedId)).Should().Be((int)PublishStatus.Cancelled, "pausing stops what has not gone out");

        var paused = await repo.FindByIdAsync(seriesId, default);
        paused!.Status.Should().Be(SeriesStatus.Paused);
        paused.NextOccurrenceUtc.Should().BeNull();
        (await repo.ListDueAsync(DateTime.UtcNow.AddYears(1), 500, default)).Should().NotContain(x => x.Id == seriesId);
    }

    [Fact]
    public async Task The_plan_limit_is_enforced_by_the_api()
    {
        RequireDatabase();

        var (free, _, freePoster, freeConn) = await PharmacyAsync("series-free", tier: "Free");
        using var refused = await free.PostAsJsonAsync("/api/social/series", Body(freePoster, freeConn));
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, "Free has no recurring posts");

        var (starter, _, poster, conn) = await PharmacyAsync("series-starter", tier: "Starter");
        await CreateSeriesAsync(starter, poster, conn);
        await CreateSeriesAsync(starter, poster, conn);
        using var third = await starter.PostAsJsonAsync("/api/social/series", Body(poster, conn));
        third.StatusCode.Should().Be(HttpStatusCode.Forbidden, "Starter allows two active series");
    }

    [Fact]
    public async Task A_stranger_cannot_see_or_pause_someone_elses_series()
    {
        RequireDatabase();
        var (owner, _, poster, conn) = await PharmacyAsync("series-owner");
        var seriesId = await CreateSeriesAsync(owner, poster, conn);
        var (stranger, _, _) = await Api.RegisterPharmacyAsync("series-stranger");

        using var get = await stranger.GetAsync($"/api/social/series/{seriesId}");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var pause = await stranger.PostAsync($"/api/social/series/{seriesId}/pause", null);
        pause.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
