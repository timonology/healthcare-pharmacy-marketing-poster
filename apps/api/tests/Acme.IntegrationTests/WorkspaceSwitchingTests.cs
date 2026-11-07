using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// Working as more than one pharmacy.
/// </summary>
/// <remarks>
/// <para>
/// The membership check is the whole security boundary of this feature. A
/// switch issues a token whose subject is another account, and every endpoint
/// in the product reads that subject to decide whose posters, contacts and
/// campaigns it is looking at. If the gate is wrong, it is wrong for
/// everything at once.
/// </para>
/// <para>
/// So these tests do not stop at "the switch was refused". They switch, then
/// read and write, and check the data landed where the token said it would —
/// because a gate that returns the right status code and the wrong token would
/// pass a weaker test.
/// </para>
/// </remarks>
public sealed class WorkspaceSwitchingTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static void Bearer(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<JsonElement> WorkspacesAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/workspaces", Json);

    private async Task<HttpResponseMessage> SwitchAsync(HttpClient client, string pharmacyId) =>
        await client.PostAsJsonAsync("/api/workspaces/switch", new { pharmacyId });

    /* ── the list ────────────────────────────────────────────────────── */

    [Fact]
    public async Task A_new_account_can_work_as_its_own_pharmacy()
    {
        // The self-membership. Without it somebody cannot open their own
        // pharmacy — total failure, visible only to the person affected.
        RequireDatabase();

        var (client, _, userId) = await Api.RegisterPharmacyAsync("solo");

        var body = await WorkspacesAsync(client);

        body.GetProperty("currentPharmacyId").GetString().Should().Be(userId);
        body.GetProperty("isActingAsAnother").GetBoolean().Should().BeFalse();

        var list = body.GetProperty("workspaces").EnumerateArray().ToList();
        list.Should().ContainSingle("a fresh account belongs to exactly one pharmacy");
        list[0].GetProperty("isOwn").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task The_list_does_not_include_pharmacies_you_do_not_belong_to()
    {
        RequireDatabase();

        var (mine, _, _) = await Api.RegisterPharmacyAsync("list-mine");
        var (_, _, strangerId) = await Api.RegisterPharmacyAsync("list-stranger");

        var body = await WorkspacesAsync(mine);
        var ids = body.GetProperty("workspaces").EnumerateArray()
            .Select(w => w.GetProperty("pharmacyId").GetString())
            .ToList();

        ids.Should().NotContain(strangerId);
    }

    /* ── the gate ────────────────────────────────────────────────────── */

    [Fact]
    public async Task Switching_to_a_pharmacy_you_do_not_belong_to_is_refused()
    {
        // The one that matters. A pharmacy id is a request, not a fact.
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("intruder");
        var (_, _, victimId) = await Api.RegisterPharmacyAsync("switch-victim");

        using var res = await SwitchAsync(client, victimId);

        res.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "membership is the gate, and this caller has none for that pharmacy");
    }

    [Fact]
    public async Task A_refused_switch_does_not_reveal_whether_the_pharmacy_exists()
    {
        // Probing. If a real id and an invented one answer differently, the
        // endpoint confirms which account ids are real.
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("prober");
        var (_, _, realId) = await Api.RegisterPharmacyAsync("prober-target");

        using var real = await SwitchAsync(client, realId);
        using var invented = await SwitchAsync(client, Guid.NewGuid().ToString("N"));

        invented.StatusCode.Should().Be(
            real.StatusCode,
            "a real pharmacy you cannot reach and one that does not exist must look alike");

        var realBody = await real.Content.ReadAsStringAsync();
        var inventedBody = await invented.Content.ReadAsStringAsync();
        inventedBody.Should().Be(realBody);
    }

    [Fact]
    public async Task Switching_to_nothing_is_refused()
    {
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("empty-switch");

        using var res = await SwitchAsync(client, "");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Switching_requires_a_session()
    {
        RequireDatabase();

        var anon = Api.NewClient();
        using var res = await SwitchAsync(anon, Guid.NewGuid().ToString("N"));

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /* ── switching for real ──────────────────────────────────────────── */

    /// <summary>Registers a parent with one child, which the backfill makes a membership.</summary>
    private async Task<(HttpClient Parent, string ParentId, string ChildId, string ChildName)>
        ParentWithChildAsync(string label)
    {
        var (parent, _, parentId) = await Api.RegisterPharmacyAsync(label);
        await Api.UpgradeToProAsync(parentId);

        // Running branches is granted by staff, not assumed by the account, so
        // a fixture that needs a group has to arrange it — exactly as it has to
        // arrange a paid plan.
        await Api.GrantHeadOfficeAsync(parentId);

        var childName = $"Branch {label}";
        using var created = await parent.PostAsJsonAsync("/api/pharmacies/children", new
        {
            pharmacyName = childName,
            email = $"child-{label}-{Guid.NewGuid().ToString("N")[..8]}@example.test",
            fullName = "Branch Manager",
        });

        created.IsSuccessStatusCode.Should().BeTrue(
            "the test needs a sub-pharmacy: {0}", await created.Content.ReadAsStringAsync());

        var body = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        // ChildPharmacyDto names it userId. A missing property throws rather
        // than returning null, so a ?? fallback here would never run.
        var childId = body.GetProperty("userId").GetString()!;

        return (parent, parentId, childId, childName);
    }

    [Fact]
    public async Task A_head_office_can_switch_to_its_branch_and_back()
    {
        RequireDatabase();

        var (parent, parentId, childId, _) = await ParentWithChildAsync("switch-real");

        using var toChild = await SwitchAsync(parent, childId);
        toChild.IsSuccessStatusCode.Should().BeTrue(
            "a parent holds a HeadOfficeAdmin membership of its child: {0}",
            await toChild.Content.ReadAsStringAsync());

        var switched = await toChild.Content.ReadFromJsonAsync<JsonElement>(Json);
        var token = switched.GetProperty("token").GetString()!;
        switched.GetProperty("pharmacyId").GetString().Should().Be(childId);

        // Use the token: /api/me must now answer as the child.
        var asChild = Api.NewClient();
        Bearer(asChild, token);

        var me = await asChild.GetFromJsonAsync<JsonElement>("/api/me", Json);
        me.GetProperty("id").GetString().Should().Be(
            childId, "the switched token must actually address the branch");

        // And back again, while holding the delegated token.
        using var back = await SwitchAsync(asChild, parentId);
        back.IsSuccessStatusCode.Should().BeTrue(
            "switching back must work while already switched, or there is no way home: {0}",
            await back.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Data_written_after_a_switch_belongs_to_the_pharmacy_switched_to()
    {
        // The property the whole feature rests on. A gate that returns the
        // right status and the wrong token would satisfy every test above.
        RequireDatabase();

        var (parent, parentId, childId, _) = await ParentWithChildAsync("write-scope");

        using var switched = await SwitchAsync(parent, childId);
        var token = (await switched.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("token").GetString()!;

        var asChild = Api.NewClient();
        Bearer(asChild, token);

        using var created = await asChild.PostAsJsonAsync(
            "/api/posters", new { name = "Made while working as the branch" });
        created.IsSuccessStatusCode.Should().BeTrue();

        // The branch sees it.
        var childPosters = await asChild.GetFromJsonAsync<JsonElement>("/api/posters?take=50", Json);
        Names(childPosters).Should().Contain("Made while working as the branch");

        // The head office, in its own context, does not.
        var parentPosters = await parent.GetFromJsonAsync<JsonElement>("/api/posters?take=50", Json);
        Names(parentPosters).Should().NotContain(
            "Made while working as the branch",
            "work done as the branch belongs to the branch, not to whoever was driving");

        parentId.Should().NotBe(childId);
    }

    [Fact]
    public async Task The_workspace_list_reflects_the_pharmacy_you_switched_to()
    {
        RequireDatabase();

        var (parent, _, childId, _) = await ParentWithChildAsync("list-after-switch");

        using var switched = await SwitchAsync(parent, childId);
        var token = (await switched.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("token").GetString()!;

        var asChild = Api.NewClient();
        Bearer(asChild, token);

        var body = await WorkspacesAsync(asChild);

        body.GetProperty("currentPharmacyId").GetString().Should().Be(childId);
        body.GetProperty("isActingAsAnother").GetBoolean().Should().BeTrue(
            "the header must be able to say you are not in your own pharmacy");

        // The list is still the *person's* pharmacies, read from the actor
        // claim — not the branch's, which would strand them there.
        var ids = body.GetProperty("workspaces").EnumerateArray()
            .Select(w => w.GetProperty("pharmacyId").GetString()).ToList();

        ids.Should().Contain(childId);
        ids.Count.Should().BeGreaterThan(
            1, "the way back to your own pharmacy has to remain visible");
    }

    [Fact]
    public async Task Deactivating_a_branch_ends_a_session_working_as_it_but_not_the_head_offices_own()
    {
        // The session check (R2-09) treats the two parties of a delegated token
        // differently. The branch need not be able to sign in — the fixture's
        // branch is invited and has never activated — but it must not be
        // deactivated. The head office's own session is untouched either way.
        RequireDatabase();

        var (parent, _, childId, _) = await ParentWithChildAsync("branch-deactivated");

        using var switched = await SwitchAsync(parent, childId);
        var token = (await switched.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("token").GetString()!;

        var asChild = Api.NewClient();
        Bearer(asChild, token);

        using var before = await asChild.GetAsync("/api/me");
        before.StatusCode.Should().Be(
            HttpStatusCode.OK, "an invited branch is still a workspace its head office can run");

        var admin = await Api.SignInSuperAdminAsync();
        using var deactivate = await admin.PostAsJsonAsync($"/api/admin/users/{childId}/deactivate", new { });
        deactivate.IsSuccessStatusCode.Should().BeTrue(await deactivate.Content.ReadAsStringAsync());

        using var after = await asChild.GetAsync("/api/me");
        after.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized, "a deactivated pharmacy cannot be worked as, even by a token already issued");

        using var own = await parent.GetAsync("/api/me");
        own.StatusCode.Should().Be(
            HttpStatusCode.OK, "deactivating a branch does not sign out its head office");
    }

    /* ── platform staff, and the accounts they may not become ────────── */

    [Fact]
    public async Task Platform_staff_can_work_as_any_pharmacy_without_a_membership()
    {
        // Support reaches pharmacies that have never granted them anything.
        // The membership gate is a pharmacy-side grant; staff need none.
        RequireDatabase();

        var (pharmacy, _, pharmacyId) = await Api.RegisterPharmacyAsync("support-target");
        var admin = await Api.SignInSuperAdminAsync();

        using var res = await SwitchAsync(admin, pharmacyId);
        res.IsSuccessStatusCode.Should().BeTrue(
            "platform staff support customers who have granted them nothing: {0}",
            await res.Content.ReadAsStringAsync());

        var token = (await res.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("token").GetString()!;

        var asPharmacy = Api.NewClient();
        Bearer(asPharmacy, token);

        var me = await asPharmacy.GetFromJsonAsync<JsonElement>("/api/me", Json);
        me.GetProperty("id").GetString().Should().Be(pharmacyId);

        _ = pharmacy;
    }

    [Fact]
    public async Task Nobody_can_work_as_a_platform_staff_account()
    {
        // A pharmacy-side privilege must never reach an administrator, and
        // staff acting as staff would launder one person's actions into
        // another's name. The check sits where the token is minted, so however
        // a stray membership came to exist it cannot be spent.
        RequireDatabase();

        var admin = await Api.SignInSuperAdminAsync();
        var superAdminId = (await admin.GetFromJsonAsync<JsonElement>("/api/me", Json))
            .GetProperty("id").GetString()!;

        var (pharmacy, _, _) = await Api.RegisterPharmacyAsync("staff-probe");

        using var byPharmacy = await SwitchAsync(pharmacy, superAdminId);
        byPharmacy.StatusCode.Should().Be(
            HttpStatusCode.Forbidden, "a pharmacy must not become an administrator");

        // And the SuperAdmin cannot become itself-via-switch either, which
        // would be a no-op, but must not become another staff account.
        var second = await Api.SignInSuperAdminAsync();
        using var byStaff = await SwitchAsync(second, superAdminId);
        ((int)byStaff.StatusCode).Should().BeInRange(
            200, 299, "switching to your own account is allowed and is a no-op");
    }

    [Fact]
    public async Task Starting_and_stopping_are_both_recorded()
    {
        // "Who was actually at the keyboard" is unanswerable afterwards
        // without these two entries, because from the moment a switch happens
        // every action is recorded as the pharmacy's own work.
        RequireDatabase();

        var (_, pharmacyEmail, pharmacyId) = await Api.RegisterPharmacyAsync("audited");
        var admin = await Api.SignInSuperAdminAsync();

        using var started = await SwitchAsync(admin, pharmacyId);
        started.IsSuccessStatusCode.Should().BeTrue();

        var token = (await started.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("token").GetString()!;

        var acting = Api.NewClient();
        Bearer(acting, token);
        using var stopped = await acting.PostAsync("/api/workspaces/stop", null);
        ((int)stopped.StatusCode).Should().BeInRange(200, 299);

        // AuditEntryDto carries targetEmail, not targetUserId — and Action is
        // serialised as a string because the host registers
        // JsonStringEnumConverter.
        var log = await admin.GetFromJsonAsync<JsonElement>(
            "/api/admin/audit?take=100", Json);

        var actions = log.GetProperty("items")
            .EnumerateArray()
            .Where(e => e.GetProperty("targetEmail").GetString() == pharmacyEmail)
            .Select(e => e.GetProperty("action").GetString())
            .ToList();

        _ = pharmacyId;

        actions.Should().Contain("StartActingAs");
        actions.Should().Contain("StopActingAs");
    }

    private static IEnumerable<string?> Names(JsonElement page)
    {
        var items = page.TryGetProperty("items", out var i) ? i : page;
        return items.EnumerateArray().Select(p => p.GetProperty("name").GetString());
    }
}
