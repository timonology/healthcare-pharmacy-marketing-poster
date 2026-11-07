using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// Who did what, end to end: the real pipeline, the real middleware, the real
/// collections.
/// </summary>
/// <remarks>
/// Rows are read straight from Mongo rather than through the console API, so a
/// bug in the console's filters cannot hide a bug in what was written. Every
/// assertion is scoped to an actor this test created, because the whole suite
/// writes to the same two collections.
/// </remarks>
public sealed class AuditEventsTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IMongoDatabase Db => new MongoClient(Api.MongoUrl).GetDatabase(Api.DatabaseName);
    private IMongoCollection<BsonDocument> Audit => Db.GetCollection<BsonDocument>("audit_events");
    private IMongoCollection<BsonDocument> Activity => Db.GetCollection<BsonDocument>("activity_events");

    private static FilterDefinition<BsonDocument> By(string actorId, string? action = null)
    {
        var f = Builders<BsonDocument>.Filter.Eq("actorId", actorId);
        return action is null ? f : f & Builders<BsonDocument>.Filter.Eq("action", action);
    }

    /// <summary>The row is written as the response completes; allow it a moment.</summary>
    private static async Task<List<BsonDocument>> Eventually(
        IMongoCollection<BsonDocument> col, FilterDefinition<BsonDocument> filter, int atLeast = 1)
    {
        var rows = new List<BsonDocument>();
        for (var i = 0; i < 20; i++)
        {
            rows = await col.Find(filter).ToListAsync();
            if (rows.Count >= atLeast) return rows;
            await Task.Delay(100);
        }
        return rows;
    }

    private async Task<string> CreatePosterAsync(HttpClient client, string name)
    {
        using var res = await client.PostAsJsonAsync("/api/posters", new { name });
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetString()!;
    }

    private static async Task<string> MyIdAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/me", Json)).GetProperty("id").GetString()!;

    [Fact]
    public async Task A_mutation_writes_one_audit_row_naming_the_entity()
    {
        RequireDatabase();
        var (client, email, id) = await Api.RegisterPharmacyAsync("audit-mut");

        var posterId = await CreatePosterAsync(client, "Audited poster");

        var rows = await Eventually(Audit, By(id, "poster.create"));
        rows.Should().ContainSingle();
        var row = rows[0];
        row["result"].AsString.Should().Be("ok");
        row["actorKind"].AsString.Should().Be("pharmacy");
        row["actorEmail"].AsString.Should().Be(email);
        row["pharmacyId"].AsString.Should().Be(id);
        row["entityType"].AsString.Should().Be("poster");
        row["entityId"].AsString.Should().Be(posterId, "a create has no id in the route; it comes from the response");
        row["requestId"].AsString.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Two_views_inside_a_minute_are_one_activity_row()
    {
        RequireDatabase();
        var (client, _, id) = await Api.RegisterPharmacyAsync("audit-view");
        var posterId = await CreatePosterAsync(client, "Viewed poster");

        (await client.GetAsync($"/api/posters/{posterId}")).EnsureSuccessStatusCode();
        (await client.GetAsync($"/api/posters/{posterId}")).EnsureSuccessStatusCode();

        var views = await Eventually(Activity, By(id, "poster.view"));
        await Task.Delay(300); // a second row, if there were going to be one
        views = await Activity.Find(By(id, "poster.view")).ToListAsync();

        views.Should().ContainSingle();
        views[0]["entityId"].AsString.Should().Be(posterId);
        (await Audit.Find(By(id, "poster.view")).AnyAsync()).Should().BeFalse("views never go in the audit log");
    }

    [Fact]
    public async Task List_polls_and_health_checks_write_nothing()
    {
        RequireDatabase();
        var (client, _, id) = await Api.RegisterPharmacyAsync("audit-poll");

        var before = await Activity.CountDocumentsAsync(By(id)) + await Audit.CountDocumentsAsync(By(id));

        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync("/api/videos")).EnsureSuccessStatusCode();
            (await client.GetAsync("/api/posters")).EnsureSuccessStatusCode();
            (await client.GetAsync("/health")).EnsureSuccessStatusCode();
        }
        await Task.Delay(300);

        var after = await Activity.CountDocumentsAsync(By(id)) + await Audit.CountDocumentsAsync(By(id));
        after.Should().Be(before, "only allow-listed views are recorded");
    }

    [Fact]
    public async Task A_refused_request_is_recorded_as_denied()
    {
        RequireDatabase();
        var (client, _, id) = await Api.RegisterPharmacyAsync("audit-403");

        // Refused by the authorization policy, before any controller runs.
        using var res = await client.PostAsJsonAsync($"/api/admin/users/{id}/deactivate", new { });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var rows = await Eventually(Audit, By(id, "admin.user.deactivate"));
        rows.Should().ContainSingle();
        rows[0]["result"].AsString.Should().Be("denied");
        rows[0]["actorKind"].AsString.Should().Be("pharmacy");
    }

    [Fact]
    public async Task A_refused_view_is_a_security_event_not_activity()
    {
        RequireDatabase();
        var (client, _, id) = await Api.RegisterPharmacyAsync("audit-403-view");

        using var res = await client.GetAsync("/api/admin/stats");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await Eventually(Audit, By(id, "admin.console.dashboard"))).Should().ContainSingle()
            .Which["result"].AsString.Should().Be("denied");
        (await Activity.Find(By(id)).AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_sign_in_is_recorded_against_the_account()
    {
        RequireDatabase();
        var (_, email, id) = await Api.RegisterPharmacyAsync("audit-login");

        using var res = await Api.NewClient().PostAsJsonAsync("/api/auth/login", new { email, password = "wrong-password-1" });
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var row = (await Eventually(Audit, By(id, "auth.login.failed"))).Should().ContainSingle().Subject;
        row["result"].AsString.Should().Be("denied");
        row["meta"]["reason"].AsString.Should().Be("bad_password");
        row.ToJson().Should().NotContain("wrong-password-1", "a password is never stored, even a wrong one");
    }

    [Fact]
    public async Task Managing_a_pharmacy_is_recorded_as_the_admin_with_the_target_shop()
    {
        RequireDatabase();
        var (_, _, shopId) = await Api.RegisterPharmacyAsync("audit-manage");
        var admin = await Api.SignInSuperAdminAsync();
        var adminId = await MyIdAsync(admin);

        using var switched = await admin.PostAsJsonAsync("/api/workspaces/switch", new { pharmacyId = shopId });
        switched.IsSuccessStatusCode.Should().BeTrue(await switched.Content.ReadAsStringAsync());
        var token = (await switched.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString();

        var start = (await Eventually(Audit, By(adminId, "admin.impersonate.start")
                        & Builders<BsonDocument>.Filter.Eq("pharmacyId", shopId)))
            .Should().ContainSingle().Subject;
        start["actorKind"].AsString.Should().Be("admin");

        // Work done inside the shop is the admin's, filed under the shop.
        var acting = Api.NewClient();
        acting.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var posterId = await CreatePosterAsync(acting, "Made by support");

        var made = (await Eventually(Audit, By(adminId, "poster.create")
                       & Builders<BsonDocument>.Filter.Eq("entityId", posterId)))
            .Should().ContainSingle().Subject;
        made["actorKind"].AsString.Should().Be("admin");
        made["pharmacyId"].AsString.Should().Be(shopId);
        made["actorRole"].AsString.Should().Be("SuperAdmin");

        (await acting.PostAsync("/api/workspaces/stop", null)).EnsureSuccessStatusCode();
        (await Eventually(Audit, By(adminId, "admin.impersonate.stop")
                & Builders<BsonDocument>.Filter.Eq("pharmacyId", shopId)))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task Seeders_are_system_and_stay_off_the_default_security_view()
    {
        RequireDatabase();

        var seeded = await Audit.Find(Builders<BsonDocument>.Filter.Eq("action", "system.seed.user")).ToListAsync();
        seeded.Should().NotBeEmpty("the development seeders ran when the host booted");
        seeded.Should().OnlyContain(r => r["actorKind"].AsString == "system");

        var admin = await Api.SignInSuperAdminAsync();

        var security = await admin.GetFromJsonAsync<JsonElement>(
            "/api/admin/audit-events?tab=Security&take=200", Json);
        security.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("actorKind").GetString())
            .Should().NotContain("system");

        var system = await admin.GetFromJsonAsync<JsonElement>(
            "/api/admin/audit-events?tab=Security&view=System&action=system.seed&take=5", Json);
        system.GetProperty("total").GetInt64().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_request_id_is_echoed_and_recorded()
    {
        RequireDatabase();
        var (client, _, id) = await Api.RegisterPharmacyAsync("audit-rid");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/posters")
        {
            Content = JsonContent.Create(new { name = "Traced poster" }),
        };
        req.Headers.Add("X-Request-Id", "trace-abc.123");
        using var res = await client.SendAsync(req);

        res.Headers.GetValues("X-Request-Id").Should().ContainSingle().Which.Should().Be("trace-abc.123");
        (await Eventually(Audit, By(id, "poster.create"))).Should().ContainSingle()
            .Which["requestId"].AsString.Should().Be("trace-abc.123");
    }

    [Fact]
    public async Task A_pharmacy_cannot_read_the_console()
    {
        RequireDatabase();
        var (client, _, _) = await Api.RegisterPharmacyAsync("audit-console");

        using var res = await client.GetAsync("/api/admin/audit-events?tab=Pharmacy");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
