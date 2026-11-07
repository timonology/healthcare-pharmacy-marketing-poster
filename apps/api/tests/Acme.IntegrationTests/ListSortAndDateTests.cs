using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// Sort and date range on every searchable list, through the real endpoints and
/// the real Mongo queries.
/// </summary>
/// <remarks>
/// <para>
/// Each test makes three items through the API, then moves their list date in
/// the database to 1, 10 and 20 January 2001 — a month nothing else in the
/// suite lives in — so the assertions cannot be disturbed by whatever other
/// tests left behind. Then, for each list:
/// </para>
/// <list type="bullet">
/// <item>newest first (the default) returns the later item first, and
/// <c>sort=oldest</c> reverses it;</item>
/// <item><c>from=2001-01-05&amp;to=2001-01-15</c> returns only the middle one;</item>
/// <item>the same request without the range returns all three again.</item>
/// </list>
/// <para>
/// Lists that belong to one pharmacy are checked exactly. The shared ones —
/// templates, the admin user table, the audit log — are narrowed to this test's
/// rows by the range itself (or by a search term unique to the run), so the
/// order assertions are still exact.
/// </para>
/// </remarks>
public sealed class ListSortAndDateTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly DateTime A = new(2001, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime B = new(2001, 1, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime C = new(2001, 1, 20, 12, 0, 0, DateTimeKind.Utc);

    private const string Month = "from=2001-01-01T00:00:00Z&to=2001-02-01T00:00:00Z";
    private const string Middle = "from=2001-01-05T00:00:00Z&to=2001-01-15T00:00:00Z";

    private IMongoDatabase Db => new MongoClient(Api.MongoUrl).GetDatabase(Api.DatabaseName);

    private Task SetDateAsync(string collection, string id, string field, DateTime at) =>
        Db.GetCollection<BsonDocument>(collection).UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            Builders<BsonDocument>.Update.Set(field, at));

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        using var res = await client.GetAsync(url);
        res.StatusCode.Should().Be(HttpStatusCode.OK, $"{url}: {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    /// <summary>Ids in the order returned, from either a bare array or a paged body.</summary>
    private static List<string> Ids(JsonElement body)
    {
        var items = body.ValueKind == JsonValueKind.Array ? body : body.GetProperty("items");
        return items.EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToList();
    }

    private static async Task<string> CreateAsync(HttpClient client, string url, object body)
    {
        using var res = await client.PostAsJsonAsync(url, body);
        res.IsSuccessStatusCode.Should().BeTrue($"POST {url}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetString()!;
    }

    /// <summary>
    /// The three behaviours, for one list. <paramref name="list"/> takes the
    /// query string to add and returns the ids in the order the API gave them.
    /// </summary>
    /// <param name="baseQuery">
    /// Always present: narrows a shared list to this test's rows. For a list
    /// that only holds this test's rows, empty.
    /// </param>
    /// <param name="clearedQuery">
    /// The "filter cleared" request. Same as <paramref name="baseQuery"/> for
    /// a private list; for a shared one, whatever still finds all three
    /// without the date range (a unique search term), or null to page the
    /// whole list.
    /// </param>
    private static async Task AssertSortAndRangeAsync(
        Func<string, Task<List<string>>> list,
        string a, string b, string c,
        string baseQuery,
        string? clearedQuery)
    {
        static string Join(params string[] parts) => string.Join("&", parts.Where(p => p.Length > 0));

        var mine = new HashSet<string> { a, b, c };

        // Newest first is the default…
        var newest = (await list(baseQuery)).Where(mine.Contains).ToList();
        newest.Should().Equal(new[] { c, b, a }, "newest first returns the later item first");

        // …and says so when asked.
        (await list(Join(baseQuery, "sort=newest"))).Where(mine.Contains).Should().Equal(new[] { c, b, a });

        var oldest = (await list(Join(baseQuery, "sort=oldest"))).Where(mine.Contains).ToList();
        oldest.Should().Equal(new[] { a, b, c }, "oldest first reverses it");

        // A range keeps only what is inside it: from inclusive, to exclusive.
        var middle = await list(Join(baseQuery.Replace(Month, ""), Middle));
        middle.Should().Contain(b);
        middle.Should().NotContain(a, "1 January is before the range");
        middle.Should().NotContain(c, "20 January is after the range");

        // Clearing the range brings all three back.
        var cleared = await list(clearedQuery ?? "");
        cleared.Should().Contain(new[] { a, b, c }, "clearing the filter restores the full list");
    }

    [Fact]
    public async Task My_posters_sort_and_range_on_last_updated()
    {
        RequireDatabase();
        var (client, _, _) = await Api.RegisterPharmacyAsync("lw-posters");

        var a = await CreateAsync(client, "/api/posters", new { name = "Window A" });
        var b = await CreateAsync(client, "/api/posters", new { name = "Window B" });
        var c = await CreateAsync(client, "/api/posters", new { name = "Window C" });
        // The dates decide, not the insert order: see the videos test, which
        // inserts in reverse.
        await SetDateAsync("posters", a, "updatedAtUtc", A);
        await SetDateAsync("posters", b, "updatedAtUtc", B);
        await SetDateAsync("posters", c, "updatedAtUtc", C);

        await AssertSortAndRangeAsync(
            async q => Ids(await GetJsonAsync(client, $"/api/posters?take=100&{q}")),
            a, b, c, baseQuery: "", clearedQuery: "");
    }

    [Fact]
    public async Task Videos_sort_and_range_on_created()
    {
        RequireDatabase();
        var (client, _, id) = await Api.RegisterPharmacyAsync("lw-videos");

        // Seeded in the opposite order to their dates, so an answer in insert
        // order would fail rather than pass by coincidence.
        var c = await Api.SeedVideoAsync(id, "Window C", Acme.Domain.Videos.VideoStatus.Ready);
        var b = await Api.SeedVideoAsync(id, "Window B", Acme.Domain.Videos.VideoStatus.Ready);
        var a = await Api.SeedVideoAsync(id, "Window A", Acme.Domain.Videos.VideoStatus.Ready);
        await SetDateAsync("videos", a, "createdAtUtc", A);
        await SetDateAsync("videos", b, "createdAtUtc", B);
        await SetDateAsync("videos", c, "createdAtUtc", C);

        await AssertSortAndRangeAsync(
            async q => Ids(await GetJsonAsync(client, $"/api/videos?{q}")),
            a, b, c, baseQuery: "", clearedQuery: "");
    }

    [Fact]
    public async Task Patients_sort_and_range_on_date_added()
    {
        RequireDatabase();
        var (client, _, id) = await Api.RegisterPharmacyAsync("lw-patients");
        await Api.UpgradeToProAsync(id);

        // Names in the opposite order to the dates, so the old alphabetical
        // order cannot pass for newest first.
        var a = await CreateAsync(client, "/api/patients", new { fullName = "Zed Window", email = "zed@example.test" });
        var b = await CreateAsync(client, "/api/patients", new { fullName = "May Window", email = "may@example.test" });
        var c = await CreateAsync(client, "/api/patients", new { fullName = "Ann Window", email = "ann@example.test" });
        await SetDateAsync("patients", a, "createdAtUtc", A);
        await SetDateAsync("patients", b, "createdAtUtc", B);
        await SetDateAsync("patients", c, "createdAtUtc", C);

        await AssertSortAndRangeAsync(
            async q => Ids(await GetJsonAsync(client, $"/api/patients?take=200&{q}")),
            a, b, c, baseQuery: "", clearedQuery: "");
    }

    [Fact]
    public async Task Campaigns_sort_and_range_on_sent_or_else_created()
    {
        RequireDatabase();
        var (client, _, id) = await Api.RegisterPharmacyAsync("lw-campaigns");

        // Written directly: a real send needs recipients, a plan and the
        // dispatch worker, none of which this is about. Terminal statuses, so
        // the worker never picks them up.
        //  · a: sent on 1 Jan, though created long after — sent date wins.
        //  · b: never sent (stopped), created 10 Jan — created date is used.
        //  · c: sent on 20 Jan.
        var col = Db.GetCollection<BsonDocument>("campaigns");
        string Seed(string name, int status, DateTime created, DateTime? sent)
        {
            var cid = Guid.NewGuid().ToString("N");
            col.InsertOne(new BsonDocument
            {
                ["_id"] = cid,
                ["ownerId"] = id,
                ["posterId"] = "none",
                ["name"] = name,
                ["channel"] = 0,
                ["status"] = status,
                ["recipients"] = new BsonArray { "someone@example.test" },
                ["videoIds"] = new BsonArray(),
                ["deliveryReport"] = new BsonArray(),
                ["sentCount"] = sent is null ? 0 : 1,
                ["sentAtUtc"] = sent is { } s ? (BsonValue)s : BsonNull.Value,
                ["createdAtUtc"] = created,
                ["updatedAtUtc"] = created,
            });
            return cid;
        }

        const int sentStatus = 2, stoppedStatus = 3;
        var a = Seed("Window A", sentStatus, created: new DateTime(2001, 3, 1, 0, 0, 0, DateTimeKind.Utc), sent: A);
        var b = Seed("Window B", stoppedStatus, created: B, sent: null);
        var c = Seed("Window C", sentStatus, created: new DateTime(2000, 6, 1, 0, 0, 0, DateTimeKind.Utc), sent: C);

        await AssertSortAndRangeAsync(
            async q => Ids(await GetJsonAsync(client, $"/api/campaigns?{q}")),
            a, b, c, baseQuery: "", clearedQuery: "");
    }

    [Fact]
    public async Task Templates_sort_and_range_on_created()
    {
        RequireDatabase();
        var admin = await Api.SignInSuperAdminAsync();

        // Any published template's canvas will do as a body.
        var first = Ids(await GetJsonAsync(admin, "/api/templates?take=1"))[0];
        var source = await GetJsonAsync(admin, $"/api/templates/{first}");
        var canvas = source.GetProperty("canvas");
        // Passed through as JSON, so it works whether enums are sent as names or numbers.
        var category = source.GetProperty("category");

        async Task<string> Make(string name) => await CreateAsync(admin, "/api/templates", new
        {
            name,
            description = "Sort and date range test",
            category,
            tags = Array.Empty<string>(),
            canvas,
            isPublished = true,
        });

        var a = await Make("Window template A");
        var b = await Make("Window template B");
        var c = await Make("Window template C");
        await SetDateAsync("templates", a, "createdAtUtc", A);
        await SetDateAsync("templates", b, "createdAtUtc", B);
        await SetDateAsync("templates", c, "createdAtUtc", C);

        var visitor = Api.NewClient();
        async Task<List<string>> Page(string q) =>
            Ids(await GetJsonAsync(visitor, $"/api/templates?take=100&{q}"));

        // The whole gallery, paged, for the "cleared" check: it is shared and
        // may be longer than one page.
        async Task<List<string>> All(string q)
        {
            var ids = new List<string>();
            for (var skip = 0; ; skip += 100)
            {
                var page = Ids(await GetJsonAsync(visitor, $"/api/templates?take=100&skip={skip}&{q}"));
                ids.AddRange(page);
                if (page.Count < 100) return ids;
            }
        }

        await AssertSortAndRangeAsync(
            q => q.Contains("from=") ? Page(q) : All(q),
            a, b, c, baseQuery: Month, clearedQuery: null);
    }

    [Fact]
    public async Task Admin_users_and_pharmacies_sort_and_range_on_account_created()
    {
        RequireDatabase();
        var token = Guid.NewGuid().ToString("N")[..8];
        var (_, _, a) = await Api.RegisterPharmacyAsync($"lw{token}a");
        var (_, _, b) = await Api.RegisterPharmacyAsync($"lw{token}b");
        var (_, _, c) = await Api.RegisterPharmacyAsync($"lw{token}c");
        await SetDateAsync("users", a, "createdAtUtc", A);
        await SetDateAsync("users", b, "createdAtUtc", B);
        await SetDateAsync("users", c, "createdAtUtc", C);

        var admin = await Api.SignInSuperAdminAsync();

        // The Users page, and the Pharmacies page (same endpoint, pharmacy
        // accounts only). The search term is unique to this run.
        foreach (var scope in new[] { "", "pharmacyAccounts=true" })
        {
            var search = $"search=lw{token}";
            var baseQuery = string.Join("&", new[] { search, scope }.Where(p => p.Length > 0));
            await AssertSortAndRangeAsync(
                async q => Ids(await GetJsonAsync(admin, $"/api/admin/users?take=100&{q}")),
                a, b, c, baseQuery: baseQuery, clearedQuery: baseQuery);
        }
    }

    [Fact]
    public async Task Admin_users_still_accept_the_older_sort_names()
    {
        RequireDatabase();
        var admin = await Api.SignInSuperAdminAsync();

        foreach (var sort in new[] { "NewestFirst", "OldestFirst", "NameAsc", "EmailDesc", "nonsense" })
        {
            using var res = await admin.GetAsync($"/api/admin/users?sort={sort}&take=5");
            res.StatusCode.Should().Be(HttpStatusCode.OK, $"sort={sort} must not be a 400");
        }
    }

    [Fact]
    public async Task Audit_log_sorts_and_ranges_on_event_time()
    {
        RequireDatabase();
        var (client, _, id) = await Api.RegisterPharmacyAsync("lw-audit");

        // Three audited actions by one pharmacy, then their rows redated.
        await CreateAsync(client, "/api/posters", new { name = "Audit window 1" });
        await CreateAsync(client, "/api/posters", new { name = "Audit window 2" });
        await CreateAsync(client, "/api/posters", new { name = "Audit window 3" });

        var audit = Db.GetCollection<BsonDocument>("audit_events");
        var filter = Builders<BsonDocument>.Filter.Eq("actorId", id)
                     & Builders<BsonDocument>.Filter.Eq("action", "poster.create");
        var rows = new List<BsonDocument>();
        for (var i = 0; i < 30 && rows.Count < 3; i++)
        {
            rows = await audit.Find(filter).ToListAsync();
            if (rows.Count < 3) await Task.Delay(100);
        }
        rows.Should().HaveCount(3, "each create writes one audit row");

        var ids = rows.Select(r => r["_id"].AsString).ToList();
        var (a, b, c) = (ids[0], ids[1], ids[2]);
        await SetDateAsync("audit_events", a, "atUtc", A);
        await SetDateAsync("audit_events", b, "atUtc", B);
        await SetDateAsync("audit_events", c, "atUtc", C);

        var admin = await Api.SignInSuperAdminAsync();
        var scope = $"tab=Pharmacy&stream=Changes&actor={id}";
        await AssertSortAndRangeAsync(
            async q => Ids(await GetJsonAsync(admin, $"/api/admin/audit-events?take=100&{q}")),
            a, b, c, baseQuery: scope, clearedQuery: scope);
    }

    [Theory]
    [InlineData("/api/posters")]
    [InlineData("/api/videos")]
    [InlineData("/api/campaigns")]
    [InlineData("/api/patients")]
    public async Task A_bad_sort_or_date_is_not_a_server_error(string list)
    {
        RequireDatabase();
        var (client, _, _) = await Api.RegisterPharmacyAsync("lw-bad");

        using var unknownSort = await client.GetAsync($"{list}?sort=sideways");
        unknownSort.StatusCode.Should().Be(HttpStatusCode.OK, "an unknown sort is newest first");

        using var badDate = await client.GetAsync($"{list}?from=not-a-date");
        ((int)badDate.StatusCode).Should().BeLessThan(500, "a malformed date is the caller's mistake");
    }
}
