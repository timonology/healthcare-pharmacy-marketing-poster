using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Acme.IntegrationTests;

/// <summary>
/// One pharmacy must never see or touch another's records.
/// </summary>
/// <remarks>
/// <para>
/// The single most damaging class of bug this system can have, and the one
/// least likely to be noticed: everything works, every page loads, and the
/// wrong pharmacy's patients are on it. Nobody reports it, because from inside
/// one account it looks correct.
/// </para>
/// <para>
/// Every module that owns per-pharmacy data is covered by the same shape of
/// test — create as A, then try to read, modify and delete as B. The tests are
/// generated from a table rather than written out, so adding a module means
/// adding a row, and forgetting to test it is a visible omission in a list
/// rather than an absence nobody can see.
/// </para>
/// <para>
/// Two pharmacies are registered per test through the real endpoint, so the
/// two sides genuinely are different accounts. The version of this that shares
/// a fixture passes whether or not the isolation works.
/// </para>
/// </remarks>
public sealed class OwnershipIsolationTests(ApiFactory api, ITestOutputHelper output)
    : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A module that owns per-pharmacy records.</summary>
    /// <param name="Collection">List/create endpoint.</param>
    /// <param name="Build">
    /// Body that creates one, given the id of a poster the owner already has.
    /// A function rather than a constant because a campaign cannot exist
    /// without a poster — the first version of this table sent campaigns a body
    /// with no posterId, which 400'd, and the module was never actually tested.
    /// </param>
    /// <param name="NeedsPoster">Create a poster for the owner first.</param>
    /// <param name="HasRecordRoutes">
    /// Whether <c>GET/PUT/DELETE /collection/{id}</c> exist at all.
    ///
    /// Campaigns do not have them — only list, create, retry and stop. Running
    /// the per-record checks anyway would have "passed" three times over on
    /// 404s and 405s that had nothing to do with ownership, and then failed the
    /// final owner-still-has-it check for the same reason. Passing for the
    /// wrong reason is the thing this whole suite exists to stop, so a module
    /// without per-record routes is checked on its list only, and says so.
    /// </param>
    /// <param name="RequiresPaidPlan">
    /// The owner must be off the Free plan before the create will be accepted.
    /// </param>
    public sealed record Module(
        string Name,
        string Collection,
        Func<string, object> Build,
        bool NeedsPoster = false,
        bool HasRecordRoutes = true,
        bool RequiresPaidPlan = false);

    public static TheoryData<string> ModuleNames()
    {
        var data = new TheoryData<string>();
        foreach (var m in Modules) data.Add(m.Name);
        return data;
    }

    // Bodies match the real request DTOs. Extra properties would be ignored
    // silently, and missing required ones fail the create — at which point the
    // test reports that rather than passing without having tested anything.
    private static readonly Module[] Modules =
    [
        new("posters", "/api/posters",
            _ => new { name = "Isolation probe poster" }),

        new("patients", "/api/patients",
            _ => new
            {
                fullName = "Isolation Probe",
                email = "probe@example.test",
                phone = "07000000000",
            }),

        new("patient-groups", "/api/patient-groups",
            _ => new { name = "Isolation probe group" }),

        // Two things the DTO alone does not tell you:
        //
        //  · CampaignChannel is an enum, and the API registers a
        //    JsonStringEnumConverter, so the string name is the wire form.
        //  · `Recipients`, `PatientIds` and `GroupIds` are each optional on the
        //    record, but the service refuses a campaign that resolves to nobody
        //    ("No valid recipients"). Optional individually, required together —
        //    which is why sending none of them 400'd.
        //  · And then it is gated on the subscription plan, so the owner has to
        //    be upgraded before any of the above matters.
        //
        // Three preconditions, none of them visible in the request record. This
        // is why the fixture guard throws with the response body instead of
        // letting the module quietly go untested.
        new("campaigns", "/api/campaigns",
            posterId => new
            {
                posterId,
                name = "Isolation probe campaign",
                channel = "Email",
                recipients = new[] { "isolation-probe@example.test" },
            },
            NeedsPoster: true,
            HasRecordRoutes: false,
            RequiresPaidPlan: true),
    ];

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public async Task Another_pharmacy_cannot_read_or_change_these(string moduleName)
    {
        RequireDatabase();

        var module = Modules.Single(m => m.Name == moduleName);

        var (owner, _, ownerId) = await Api.RegisterPharmacyAsync($"owner-{moduleName}");
        var (stranger, _, _) = await Api.RegisterPharmacyAsync($"stranger-{moduleName}");

        // Plan first. A new account is on Free, and campaign creation is gated
        // above it — only the owner needs upgrading, because listing is not
        // gated and the stranger never creates anything.
        if (module.RequiresPaidPlan) await Api.UpgradeToProAsync(ownerId);

        // Dependencies first, as the owner, so the record under test genuinely
        // belongs to them.
        var posterId = "";
        if (module.NeedsPoster)
        {
            using var poster = await owner.PostAsJsonAsync(
                "/api/posters", new { name = $"Poster for the {moduleName} probe" });
            poster.IsSuccessStatusCode.Should().BeTrue(
                "the {0} probe needs a poster to attach to", moduleName);

            var posterBody = await poster.Content.ReadFromJsonAsync<JsonElement>(Json);
            posterId = posterBody.GetProperty("id").GetString()!;

            // Published, because the campaigns probe sends a real campaign and
            // a draft is refused. A fourth precondition for that module, and as
            // invisible in the request record as the other three.
            using var published = await owner.PatchAsJsonAsync(
                $"/api/posters/{posterId}/status", new { status = "Published" });
            published.IsSuccessStatusCode.Should().BeTrue(
                "the {0} probe needs a poster it is allowed to send", moduleName);
        }

        var payload = module.Build(posterId);

        // Create as the owner.
        using var created = await owner.PostAsJsonAsync(module.Collection, payload);
        if (!created.IsSuccessStatusCode)
        {
            // A module whose create endpoint needs more setup than this table
            // provides. Say so rather than passing quietly — a silent pass here
            // would claim isolation was verified when nothing was created.
            var detail = await created.Content.ReadAsStringAsync();
            output.WriteLine($"create -> {(int)created.StatusCode}: {detail}");
            throw new InvalidOperationException(
                $"Could not create a {moduleName} to test isolation with " +
                $"({(int)created.StatusCode}). Fix the fixture body in this file — " +
                "leaving it failing means this module is not actually covered.");
        }

        var body = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = body.GetProperty("id").GetString();
        id.Should().NotBeNullOrEmpty();

        // ── list ──────────────────────────────────────────────────────────
        // Applies to every module, and is the check that matters most: it is
        // how one pharmacy's records would show up on another's screen.
        using var list = await stranger.GetAsync(module.Collection);
        list.IsSuccessStatusCode.Should().BeTrue();
        var listBody = await list.Content.ReadAsStringAsync();
        listBody.Should().NotContain(id!,
            $"the stranger's own {moduleName} list contained another pharmacy's record");

        if (!module.HasRecordRoutes) return;

        var record = $"{module.Collection}/{id}";

        // ── read ──────────────────────────────────────────────────────────
        using var read = await stranger.GetAsync(record);
        read.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound, HttpStatusCode.Forbidden);

        // ── modify ────────────────────────────────────────────────────────
        using var update = await stranger.PutAsJsonAsync(record, payload);
        ((int)update.StatusCode).Should().NotBeInRange(200, 299,
            $"a stranger modified another pharmacy's {moduleName} record");

        // ── delete ────────────────────────────────────────────────────────
        using var delete = await stranger.DeleteAsync(record);
        ((int)delete.StatusCode).Should().NotBeInRange(200, 299,
            $"a stranger deleted another pharmacy's {moduleName} record");

        // ── and the owner still has it ────────────────────────────────────
        // The check that stops "deny everything" from passing this test. A bug
        // that refuses the owner too would satisfy every assertion above — and
        // it is what exposed that campaigns have no per-record routes at all.
        using var stillThere = await owner.GetAsync(record);
        stillThere.IsSuccessStatusCode.Should().BeTrue(
            "the owner lost access to their own record — the refusals above may " +
            "simply be refusing everyone");
    }

    [Fact]
    public async Task A_pharmacy_cannot_read_another_pharmacys_support_view()
    {
        RequireDatabase();

        var (_, _, targetId) = await Api.RegisterPharmacyAsync("support-target");
        var (stranger, _, _) = await Api.RegisterPharmacyAsync("support-stranger");

        // The support view exposes another pharmacy's patients wholesale, so it
        // is the highest-value thing in the system to get wrong.
        foreach (var tab in new[] { "overview", "patients", "groups", "posters", "campaigns" })
        {
            using var res = await stranger.GetAsync($"/api/admin/pharmacies/{targetId}/{tab}");
            ((int)res.StatusCode).Should().NotBeInRange(200, 299,
                $"a pharmacy read another pharmacy's {tab} through the admin support view");
        }
    }

    [Fact]
    public async Task A_pharmacy_cannot_manage_a_pharmacy_that_is_not_its_branch()
    {
        RequireDatabase();

        var (_, _, targetId) = await Api.RegisterPharmacyAsync("manage-target");
        var (stranger, _, _) = await Api.RegisterPharmacyAsync("manage-stranger");

        using var res = await stranger.PostAsync(
            $"/api/pharmacies/children/{targetId}/manage-as", null);

        // 404 specifically: a 403 would confirm the account exists, turning this
        // endpoint into a way to enumerate pharmacy ids.
        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an unrelated pharmacy must be indistinguishable from one that does not exist");
    }

    [Fact]
    public async Task An_ordinary_pharmacy_cannot_create_a_global_group()
    {
        RequireDatabase();

        var (pharmacy, _, _) = await Api.RegisterPharmacyAsync("global-group");

        using var res = await pharmacy.PostAsJsonAsync(
            "/api/admin/global-groups", new { name = "Should never exist" });

        ((int)res.StatusCode).Should().NotBeInRange(200, 299,
            "a global group appears in every pharmacy's list on the platform");
    }
}
