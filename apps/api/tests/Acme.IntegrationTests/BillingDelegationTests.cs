using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// Billing belongs to the pharmacy's own account holder (security review R3-06).
/// </summary>
/// <remarks>
/// <para>
/// Somebody working as another pharmacy — a head office in one of its branches,
/// a colleague, support — may see that pharmacy's plan but not change it: no
/// checkout, no credit purchase, no billing portal (card, invoices,
/// cancellation), no plan switch. A branch pays its own bill, and the audit log
/// can name who cancelled a plan but cannot undo it.
/// </para>
/// <para>
/// Each refusal is paired with the case that must still work, so "refuse
/// everyone" cannot pass. The pairing that matters most is switching back to
/// your <i>own</i> pharmacy: that issues a delegated token too, with yourself as
/// the actor, and it must not lock you out of your own billing.
/// </para>
/// </remarks>
public sealed class BillingDelegationTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string RefusalCode = "own_account_only";

    private HttpClient As(string token)
    {
        var client = Api.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<string> TokenFrom(HttpResponseMessage switched)
    {
        switched.IsSuccessStatusCode.Should().BeTrue(await switched.Content.ReadAsStringAsync());
        return (await switched.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString()!;
    }

    /// <summary>A head office on Pro with one branch on Free.</summary>
    private async Task<(HttpClient HeadOffice, string HeadOfficeId, string BranchId)> GroupAsync(string label)
    {
        var (headOffice, _, headOfficeId) = await Api.RegisterPharmacyAsync(label);
        await Api.UpgradeToProAsync(headOfficeId);
        await Api.GrantHeadOfficeAsync(headOfficeId);

        using var created = await headOffice.PostAsJsonAsync("/api/pharmacies/children", new
        {
            pharmacyName = $"Branch {label}",
            email = $"branch-{label}-{Guid.NewGuid().ToString("N")[..8]}@example.test",
            fullName = "Branch Manager",
        });
        created.IsSuccessStatusCode.Should().BeTrue(await created.Content.ReadAsStringAsync());
        var branchId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("userId").GetString()!;

        return (headOffice, headOfficeId, branchId);
    }

    /// <summary>Every request that changes a plan or reaches the card.</summary>
    private static IEnumerable<(string Name, Func<HttpClient, Task<HttpResponseMessage>> Send)> MoneyActions() =>
    [
        ("checkout", c => c.PostAsJsonAsync("/api/billing/checkout", new { tier = "Pro", interval = "Monthly" })),
        ("credit pack", c => c.PostAsJsonAsync("/api/billing/credits", new { packKey = "videos-10" })),
        ("billing portal", c => c.PostAsync("/api/billing/portal", null)),
        ("plan switch", c => c.PostAsJsonAsync("/api/subscription/upgrade", new { tier = "Pro" })),
    ];

    [Fact]
    public async Task Working_as_a_branch_cannot_touch_its_billing()
    {
        RequireDatabase();
        var (headOffice, _, branchId) = await GroupAsync("bill-refused");
        var asBranch = As(await TokenFrom(
            await headOffice.PostAsJsonAsync("/api/workspaces/switch", new { pharmacyId = branchId })));
        var tierBefore = await TierAsync(asBranch);

        foreach (var (name, send) in MoneyActions())
        {
            using var res = await send(asBranch);
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "{0} while working as a branch", name);

            var body = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
            body.GetProperty("code").GetString().Should().Be(RefusalCode,
                "the web app reads the code to explain the refusal ({0})", name);
            body.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        }

        // And nothing changed behind the refusal: with Stripe off, the plan
        // switch would otherwise have moved the branch to Pro for nothing.
        (await TierAsync(asBranch)).Should().Be(tierBefore);
        tierBefore.Should().NotBe("Pro", "otherwise the check above proves nothing");
    }

    private static async Task<string?> TierAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/subscription/current", Json))
            .GetProperty("plan").GetProperty("tier").GetString();

    [Fact]
    public async Task Working_as_a_branch_still_shows_its_plan_and_says_it_is_read_only()
    {
        RequireDatabase();
        var (headOffice, _, branchId) = await GroupAsync("bill-visible");
        var asBranch = As(await TokenFrom(
            await headOffice.PostAsJsonAsync("/api/workspaces/switch", new { pharmacyId = branchId })));

        using var res = await asBranch.GetAsync("/api/billing/status");
        res.StatusCode.Should().Be(HttpStatusCode.OK, "a head office may see that a branch is out of videos");

        var status = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        status.GetProperty("tier").GetString().Should().Be(
            await TierAsync(asBranch), "it is the branch's plan, not the head office's");
        status.GetProperty("canManageBilling").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task The_account_holder_still_manages_their_own_billing()
    {
        // The pair to the refusals above: a rule that refused everyone would
        // pass them all.
        RequireDatabase();
        var (headOffice, _, _) = await GroupAsync("bill-own");

        var status = await headOffice.GetFromJsonAsync<JsonElement>("/api/billing/status", Json);
        status.GetProperty("canManageBilling").GetBoolean().Should().BeTrue();

        foreach (var (name, send) in MoneyActions())
        {
            using var res = await send(headOffice);
            res.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
                "{0} on your own account (it may fail for other reasons — Stripe is off here — but not this one)", name);
        }
    }

    [Fact]
    public async Task Switching_back_to_your_own_pharmacy_is_not_working_as_someone_else()
    {
        // The workspace switcher issues a delegated token even for your own
        // pharmacy, with you as both actor and subject. Treating "has an actor"
        // as "is someone else" would lock people out of their own billing — and
        // out of creating branches — after an ordinary switch home.
        RequireDatabase();
        var (headOffice, headOfficeId, branchId) = await GroupAsync("bill-home");

        var asBranch = As(await TokenFrom(
            await headOffice.PostAsJsonAsync("/api/workspaces/switch", new { pharmacyId = branchId })));
        var home = As(await TokenFrom(
            await asBranch.PostAsJsonAsync("/api/workspaces/switch", new { pharmacyId = headOfficeId })));

        var status = await home.GetFromJsonAsync<JsonElement>("/api/billing/status", Json);
        status.GetProperty("canManageBilling").GetBoolean().Should().BeTrue();

        using var portal = await home.PostAsync("/api/billing/portal", null);
        portal.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "it is your own billing");

        using var branch = await home.PostAsJsonAsync("/api/pharmacies/children", new
        {
            pharmacyName = "Branch made after switching home",
            email = $"branch-home-{Guid.NewGuid().ToString("N")[..8]}@example.test",
            fullName = "Branch Manager",
        });
        branch.IsSuccessStatusCode.Should().BeTrue(
            "back in your own account you are not managing another pharmacy: {0}",
            await branch.Content.ReadAsStringAsync());
    }
}
