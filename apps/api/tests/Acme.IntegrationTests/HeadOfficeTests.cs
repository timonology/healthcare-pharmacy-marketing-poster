using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// Running branches is granted, and looking into one is recorded.
/// </summary>
/// <remarks>
/// <para>
/// Head office used to be emergent: any pharmacy could add a sub-pharmacy, and
/// so any pharmacy could create a real login for another business and email a
/// credential to whatever address was typed. It is now a status Marketing25
/// grants.
/// </para>
/// <para>
/// The second half of this file is the part that matters under GDPR. A head
/// office can read a branch's records without switching into it — and switching
/// is what used to leave the audit trail. If reading without switching were not
/// recorded, it would be the quiet way around the only mechanism that answers
/// "who looked at this pharmacy's patients".
/// </para>
/// </remarks>
public sealed class HeadOfficeTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static object Branch(string label) => new
    {
        fullName = $"Branch {label}",
        email = $"branch-{label}-{Guid.NewGuid():N}@example.test",
        pharmacyName = $"Branch {label} Pharmacy",
    };

    /* ── the grant ───────────────────────────────────────────────────── */

    [Fact]
    public async Task A_pharmacy_cannot_add_branches_until_it_is_granted()
    {
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("no-grant");

        using var refused = await client.PostAsJsonAsync("/api/pharmacies/children", Branch("a"));

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "running branches is a granted capability, not one an account takes");

        var body = await refused.Content.ReadAsStringAsync();
        body.ToLowerInvariant().Should().Contain("head office",
            "the refusal names the thing that is missing");
        body.ToLowerInvariant().Should().Contain("support",
            "and says how to get it, rather than leaving somebody with nowhere to go");
    }

    [Fact]
    public async Task Granting_it_is_what_makes_branches_possible()
    {
        RequireDatabase();

        var (client, _, userId) = await Api.RegisterPharmacyAsync("granted");

        using (var before = await client.PostAsJsonAsync("/api/pharmacies/children", Branch("b1")))
        {
            before.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        await Api.GrantHeadOfficeAsync(userId);

        using var after = await client.PostAsJsonAsync("/api/pharmacies/children", Branch("b2"));
        after.IsSuccessStatusCode.Should().BeTrue(
            "the same request, accepted once the grant exists: {0}",
            await after.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_grant_is_visible_to_the_pharmacy_itself()
    {
        RequireDatabase();

        var (client, _, userId) = await Api.RegisterPharmacyAsync("context");

        using (var before = await client.GetAsync("/api/pharmacies/context"))
        {
            var dto = await before.Content.ReadFromJsonAsync<JsonElement>(Json);
            dto.GetProperty("isHeadOffice").GetBoolean().Should().BeFalse();
        }

        await Api.GrantHeadOfficeAsync(userId);

        using var after = await client.GetAsync("/api/pharmacies/context");
        (await after.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("isHeadOffice").GetBoolean()
            .Should().BeTrue("the app cannot show a badge for a fact it is never told");
    }

    [Fact]
    public async Task Withdrawing_the_grant_leaves_existing_branches_attached()
    {
        RequireDatabase();

        var (client, _, userId) = await Api.RegisterPharmacyAsync("withdraw");
        await Api.GrantHeadOfficeAsync(userId);

        using (var created = await client.PostAsJsonAsync("/api/pharmacies/children", Branch("keep")))
        {
            created.IsSuccessStatusCode.Should().BeTrue();
        }

        await Api.GrantHeadOfficeAsync(userId, granted: false);

        // The branch is still theirs. Silently orphaning a group would be far
        // worse than a head office that can no longer expand — detaching is a
        // separate, deliberate act.
        using var children = await client.GetAsync("/api/pharmacies/children");
        children.IsSuccessStatusCode.Should().BeTrue();
        (await children.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetArrayLength().Should().Be(1, "withdrawing the grant must not detach anything");

        // But no more may be added.
        using var blocked = await client.PostAsJsonAsync("/api/pharmacies/children", Branch("no"));
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /* ── the audited read ────────────────────────────────────────────── */

    [Fact]
    public async Task Reading_a_branch_is_recorded_in_the_audit_log()
    {
        RequireDatabase();

        var (client, _, headOfficeId) = await Api.RegisterPharmacyAsync("audited");
        await Api.GrantHeadOfficeAsync(headOfficeId);

        using var created = await client.PostAsJsonAsync("/api/pharmacies/children", Branch("read"));
        created.IsSuccessStatusCode.Should().BeTrue();

        var branch = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        var branchId = branch.GetProperty("userId").GetString()!;

        // The audit projection identifies a target by email, not by id — the id
        // is on the stored entry but is not part of what the endpoint returns.
        // Asserting on the id looked right and could never pass.
        var branchEmail = branch.GetProperty("email").GetString()!;

        using var overview = await client.GetAsync($"/api/pharmacies/children/{branchId}/overview");
        overview.IsSuccessStatusCode.Should().BeTrue(
            "a head office may read its own branch: {0}",
            await overview.Content.ReadAsStringAsync());

        // The point of the whole exercise: the read left a trace.
        // Filtered by action rather than reading the last fifty entries. The
        // whole suite writes to this log, so an unfiltered window is a test that
        // passes today and fails once fifty unrelated entries land in front of
        // the one it is looking for.
        var admin = await Api.SignInSuperAdminAsync();
        using var audit = await admin.GetAsync("/api/admin/audit?action=ViewBranchDetail&take=50");
        audit.IsSuccessStatusCode.Should().BeTrue();

        var log = await audit.Content.ReadAsStringAsync();
        log.Should().Contain("ViewBranchDetail",
            "a head office reading a branch without switching into it must still be answerable");
        log.Should().Contain(branchEmail,
            "and the entry has to say which branch was read");
    }

    [Fact]
    public async Task The_admin_list_says_which_accounts_are_branches()
    {
        RequireDatabase();

        // The admin console disables "Grant head office" on a branch, because
        // the server refuses it and a button that always fails is worse than no
        // button. That guidance is driven entirely by this field.
        //
        // It is asserted here rather than left to check-contract.mjs, which
        // compares routes and verbs between the layers but not the shape of
        // what they carry — a DTO field dropped from the mapper would pass it.
        var (client, headOfficeEmail, headOfficeId) = await Api.RegisterPharmacyAsync("shape");
        await Api.GrantHeadOfficeAsync(headOfficeId);

        using var created = await client.PostAsJsonAsync("/api/pharmacies/children", Branch("shape"));
        created.IsSuccessStatusCode.Should().BeTrue();
        var branchEmail = (await created.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("email").GetString()!;

        var admin = await Api.SignInSuperAdminAsync();
        using var list = await admin.GetAsync(
            $"/api/admin/users?search={Uri.EscapeDataString(branchEmail)}&take=5");
        list.IsSuccessStatusCode.Should().BeTrue();

        var items = (await list.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("items");
        items.GetArrayLength().Should().Be(1, "the search should find exactly the branch");

        var row = items[0];
        row.TryGetProperty("isChildPharmacy", out var isChild)
            .Should().BeTrue("the admin console cannot disable a control for a fact it is never sent");
        isChild.GetBoolean().Should().BeTrue();

        // And the parent is not a branch, so the same field distinguishes them
        // rather than being true for everyone in a hierarchy.
        //
        // Searched by exact email, not by the label: every run of this suite
        // leaves accounts behind, and a label search would eventually return a
        // page of them with the one being asserted on somewhere past the end.
        using var parentList = await admin.GetAsync(
            $"/api/admin/users?search={Uri.EscapeDataString(headOfficeEmail)}&take=5");
        var parentRow = (await parentList.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("items").EnumerateArray()
            .First(u => u.GetProperty("id").GetString() == headOfficeId);

        parentRow.GetProperty("isChildPharmacy").GetBoolean().Should().BeFalse();
        parentRow.GetProperty("isHeadOffice").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_pharmacy_cannot_read_a_branch_that_is_not_theirs()
    {
        RequireDatabase();

        var (owner, _, ownerId) = await Api.RegisterPharmacyAsync("owner");
        await Api.GrantHeadOfficeAsync(ownerId);

        using var created = await owner.PostAsJsonAsync("/api/pharmacies/children", Branch("theirs"));
        var branchId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("userId").GetString()!;

        var (stranger, _, strangerId) = await Api.RegisterPharmacyAsync("stranger");
        await Api.GrantHeadOfficeAsync(strangerId);

        using var refused = await stranger.GetAsync($"/api/pharmacies/children/{branchId}/overview");

        // Not-found rather than forbidden, deliberately: a distinguishable
        // "that exists but is not yours" lets somebody probe for other
        // pharmacies' account ids.
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "being a head office grants nothing over somebody else's branches");
    }
}
