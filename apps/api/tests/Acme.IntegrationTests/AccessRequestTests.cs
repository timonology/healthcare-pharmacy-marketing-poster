using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// Asking for access to a pharmacy, and the decision that grants it.
/// </summary>
/// <remarks>
/// Approving one of these lets an account read another pharmacy's patient
/// list, so the tests that matter are the ones about who may decide and what a
/// decision creates — not the happy path. A request that granted anything by
/// itself, or a decision anyone could make, would be the whole feature
/// backwards.
/// </remarks>
public sealed class AccessRequestTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Reason = "Covering the Tuesday locum shift at this branch.";

    private static async Task<HttpResponseMessage> RequestAsync(
        HttpClient client, string pharmacyEmail, string reason = Reason) =>
        await client.PostAsJsonAsync(
            "/api/workspaces/access-requests", new { pharmacyEmail, reason });

    private async Task<JsonElement> QueueAsync(HttpClient admin) =>
        await admin.GetFromJsonAsync<JsonElement>(
            "/api/admin/access-requests?pendingOnly=true&take=200", Json);

    /// <summary>Finds the pending request raised by this address.</summary>
    private async Task<JsonElement?> FindPendingAsync(HttpClient admin, string userEmail)
    {
        var queue = await QueueAsync(admin);
        foreach (var item in queue.EnumerateArray())
            if (item.GetProperty("userEmail").GetString() == userEmail)
                return item;
        return null;
    }

    /* ── asking ──────────────────────────────────────────────────────── */

    [Fact]
    public async Task A_request_appears_in_the_review_queue()
    {
        RequireDatabase();

        var (asker, askerEmail, _) = await Api.RegisterPharmacyAsync("req-asker");
        var (_, targetEmail, _) = await Api.RegisterPharmacyAsync("req-target");

        using var res = await RequestAsync(asker, targetEmail);
        ((int)res.StatusCode).Should().BeInRange(200, 299);

        var admin = await Api.SignInSuperAdminAsync();
        var pending = await FindPendingAsync(admin, askerEmail);

        pending.Should().NotBeNull("the request must reach somebody who can decide it");
        pending!.Value.GetProperty("status").GetString().Should().Be("Pending");
        pending.Value.GetProperty("reason").GetString().Should().Be(Reason);
    }

    [Fact]
    public async Task Asking_grants_nothing_on_its_own()
    {
        // A pending row is a piece of paper, not a key.
        RequireDatabase();

        var (asker, _, _) = await Api.RegisterPharmacyAsync("req-nogrant");
        var (_, targetEmail, targetId) = await Api.RegisterPharmacyAsync("req-nogrant-target");

        await RequestAsync(asker, targetEmail);

        using var switched = await asker.PostAsJsonAsync(
            "/api/workspaces/switch", new { pharmacyId = targetId });

        switched.StatusCode.Should().Be(
            HttpStatusCode.Forbidden, "a request is not an approval");
    }

    [Fact]
    public async Task An_unknown_address_is_answered_the_same_way()
    {
        // Otherwise this endpoint tells you which addresses are registered.
        RequireDatabase();

        var (asker, _, _) = await Api.RegisterPharmacyAsync("req-enum");
        var (_, realEmail, _) = await Api.RegisterPharmacyAsync("req-enum-target");

        using var known = await RequestAsync(asker, realEmail);
        using var unknown = await RequestAsync(
            asker, $"nobody-{Guid.NewGuid():N}@example.test");

        unknown.StatusCode.Should().Be(known.StatusCode);
        (await unknown.Content.ReadAsStringAsync())
            .Should().Be(await known.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_reason_is_required()
    {
        // An administrator approving access to somebody's patient list needs
        // something to weigh. This is the person's own input, so unlike the
        // address it is safe to complain about.
        RequireDatabase();

        var (asker, _, _) = await Api.RegisterPharmacyAsync("req-noreason");
        var (_, targetEmail, _) = await Api.RegisterPharmacyAsync("req-noreason-target");

        using var res = await RequestAsync(asker, targetEmail, "too short");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Asking_twice_does_not_queue_the_question_twice()
    {
        RequireDatabase();

        var (asker, askerEmail, _) = await Api.RegisterPharmacyAsync("req-twice");
        var (_, targetEmail, _) = await Api.RegisterPharmacyAsync("req-twice-target");

        await RequestAsync(asker, targetEmail);
        await RequestAsync(asker, targetEmail);

        var admin = await Api.SignInSuperAdminAsync();
        var queue = await QueueAsync(admin);

        var mine = queue.EnumerateArray()
            .Count(i => i.GetProperty("userEmail").GetString() == askerEmail);

        mine.Should().Be(1, "a double click must not fill the queue with duplicates");
    }

    /* ── deciding ────────────────────────────────────────────────────── */

    [Fact]
    public async Task Only_platform_staff_can_decide()
    {
        RequireDatabase();

        var (asker, askerEmail, _) = await Api.RegisterPharmacyAsync("req-whodecides");
        var (other, _, _) = await Api.RegisterPharmacyAsync("req-outsider");
        var (_, targetEmail, _) = await Api.RegisterPharmacyAsync("req-whodecides-target");

        await RequestAsync(asker, targetEmail);

        var admin = await Api.SignInSuperAdminAsync();
        var pending = await FindPendingAsync(admin, askerEmail);
        pending.Should().NotBeNull();

        var id = pending!.Value.GetProperty("id").GetString()!;

        using var byPharmacy = await other.PostAsJsonAsync(
            $"/api/admin/access-requests/{id}/review", new { approve = true });

        // The array overload, not the params one. BeOneOf(params T[]) takes every
        // argument as a candidate value, so a trailing `because` string is read
        // as another int and will not compile.
        ((int)byPharmacy.StatusCode).Should().BeOneOf(
            new[] { 401, 403, 404 },
            "a pharmacy must not approve its own way into another");
    }

    [Fact]
    public async Task Approval_is_what_creates_the_access()
    {
        RequireDatabase();

        var (asker, askerEmail, _) = await Api.RegisterPharmacyAsync("req-approve");
        var (_, targetEmail, targetId) = await Api.RegisterPharmacyAsync("req-approve-target");

        await RequestAsync(asker, targetEmail);

        var admin = await Api.SignInSuperAdminAsync();
        var pending = await FindPendingAsync(admin, askerEmail);
        var id = pending!.Value.GetProperty("id").GetString()!;

        using var decided = await admin.PostAsJsonAsync(
            $"/api/admin/access-requests/{id}/review",
            new { approve = true, note = "Confirmed with the pharmacy by phone." });

        ((int)decided.StatusCode).Should().BeInRange(
            200, 299, "the decision must be recorded: {0}", await decided.Content.ReadAsStringAsync());

        // The requester can now work as that pharmacy.
        using var switched = await asker.PostAsJsonAsync(
            "/api/workspaces/switch", new { pharmacyId = targetId });

        switched.IsSuccessStatusCode.Should().BeTrue(
            "approval grants the membership that makes switching possible: {0}",
            await switched.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_approved_member_arrives_without_the_power_to_grant()
    {
        // Somebody who had to ask for access should not arrive able to hand it
        // out. Raising them further is a separate, deliberate act.
        RequireDatabase();

        var (asker, askerEmail, _) = await Api.RegisterPharmacyAsync("req-role");
        var (_, targetEmail, targetId) = await Api.RegisterPharmacyAsync("req-role-target");

        await RequestAsync(asker, targetEmail);

        var admin = await Api.SignInSuperAdminAsync();
        var id = (await FindPendingAsync(admin, askerEmail))!.Value.GetProperty("id").GetString()!;

        using var decided = await admin.PostAsJsonAsync(
            $"/api/admin/access-requests/{id}/review", new { approve = true });
        ((int)decided.StatusCode).Should().BeInRange(200, 299);

        var body = await asker.GetFromJsonAsync<JsonElement>("/api/workspaces", Json);
        var granted = body.GetProperty("workspaces").EnumerateArray()
            .First(w => w.GetProperty("pharmacyId").GetString() == targetId);

        granted.GetProperty("canManage").GetBoolean().Should().BeFalse(
            "an approved request grants Member, never HeadOfficeAdmin");
    }

    [Fact]
    public async Task Rejection_grants_nothing()
    {
        RequireDatabase();

        var (asker, askerEmail, _) = await Api.RegisterPharmacyAsync("req-reject");
        var (_, targetEmail, targetId) = await Api.RegisterPharmacyAsync("req-reject-target");

        await RequestAsync(asker, targetEmail);

        var admin = await Api.SignInSuperAdminAsync();
        var id = (await FindPendingAsync(admin, askerEmail))!.Value.GetProperty("id").GetString()!;

        using var decided = await admin.PostAsJsonAsync(
            $"/api/admin/access-requests/{id}/review",
            new { approve = false, note = "No relationship with this pharmacy." });
        ((int)decided.StatusCode).Should().BeInRange(200, 299);

        using var switched = await asker.PostAsJsonAsync(
            "/api/workspaces/switch", new { pharmacyId = targetId });
        switched.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_decision_cannot_be_revisited()
    {
        // A request that could be re-decided would let an approval be quietly
        // replaced while the membership it created stayed behind.
        RequireDatabase();

        var (asker, askerEmail, _) = await Api.RegisterPharmacyAsync("req-redecide");
        var (_, targetEmail, _) = await Api.RegisterPharmacyAsync("req-redecide-target");

        await RequestAsync(asker, targetEmail);

        var admin = await Api.SignInSuperAdminAsync();
        var id = (await FindPendingAsync(admin, askerEmail))!.Value.GetProperty("id").GetString()!;

        using var first = await admin.PostAsJsonAsync(
            $"/api/admin/access-requests/{id}/review", new { approve = true });
        ((int)first.StatusCode).Should().BeInRange(200, 299);

        using var second = await admin.PostAsJsonAsync(
            $"/api/admin/access-requests/{id}/review", new { approve = false });
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /* ── what the requester sees ─────────────────────────────────────── */

    [Fact]
    public async Task The_requester_can_see_the_outcome()
    {
        RequireDatabase();

        var (asker, askerEmail, _) = await Api.RegisterPharmacyAsync("req-status");
        var (_, targetEmail, _) = await Api.RegisterPharmacyAsync("req-status-target");

        await RequestAsync(asker, targetEmail);

        var pendingView = await asker.GetFromJsonAsync<JsonElement>(
            "/api/workspaces/access-requests", Json);
        pendingView.EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("status").GetString().Should().Be("Pending");

        var admin = await Api.SignInSuperAdminAsync();
        var id = (await FindPendingAsync(admin, askerEmail))!.Value.GetProperty("id").GetString()!;

        await admin.PostAsJsonAsync(
            $"/api/admin/access-requests/{id}/review",
            new { approve = false, note = "Not this time." });

        var afterView = await asker.GetFromJsonAsync<JsonElement>(
            "/api/workspaces/access-requests", Json);

        var row = afterView.EnumerateArray().Single();
        row.GetProperty("status").GetString().Should().Be("Rejected");
        row.GetProperty("reviewNote").GetString().Should().Be(
            "Not this time.", "the requester should know why, not just that");
    }
}
