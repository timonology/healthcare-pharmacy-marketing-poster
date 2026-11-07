using System.Reflection;
using System.Security.Claims;
using Acme.Application.Admin;
using Acme.Application.Audit;
using Acme.Application.Auth;
using Acme.Domain.Admin;
using Acme.Domain.Users;
using Acme.Infrastructure.Audit;
using Acme.WebApi.Auditing;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Audit;

/// <summary>
/// The rules of the audit log that need no database: what may be stored, who
/// an event is attributed to, which rows a caller may see.
/// </summary>
public class AuditModelTests
{
    /* ── what never goes in ───────────────────────────────────────────── */

    [Theory]
    [InlineData("password")]
    [InlineData("newPassword")]
    [InlineData("accessToken")]
    [InlineData("jwt")]
    [InlineData("Authorization")]
    [InlineData("cardNumber")]
    [InlineData("recipients")]
    [InlineData("patients")]
    [InlineData("emails")]
    public void Sensitive_keys_are_dropped(string key)
    {
        var clean = AuditMeta.Sanitize(new Dictionary<string, string> { [key] = "anything" });
        clean.Should().NotContainKey(key);
    }

    [Fact]
    public void Counts_of_sensitive_things_are_kept()
    {
        var clean = AuditMeta.Sanitize(new Dictionary<string, string>
        {
            ["recipientCount"] = "1200",
            ["patientCount"] = "3",
        });

        clean.Should().Contain("recipientCount", "1200").And.Contain("patientCount", "3");
    }

    [Fact]
    public void Token_and_card_shaped_values_are_redacted_whatever_the_key()
    {
        var clean = AuditMeta.Sanitize(new Dictionary<string, string>
        {
            ["note"] = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9.c2lnbmF0dXJl",
            ["ref"] = "4242 4242 4242 4242",
        });

        clean["note"].Should().Be("[redacted]");
        clean["ref"].Should().Be("[redacted]");
    }

    [Fact]
    public void A_list_of_addresses_becomes_a_redaction_not_a_list()
    {
        var clean = AuditMeta.Sanitize(new Dictionary<string, string>
        {
            ["to"] = "a@example.test, b@example.test",
            ["target"] = "one@example.test",
        });

        clean["to"].Should().Be("[list redacted]");
        clean["target"].Should().Be("one@example.test", "one address names the target; it is not a list");
    }

    [Fact]
    public void Meta_is_small_and_cannot_inject_mongo_operators()
    {
        var big = Enumerable.Range(0, 30).ToDictionary(i => $"k{i}", i => new string('x', 500));
        big["$where"] = "1";
        big["a.b"] = "1";

        var clean = AuditMeta.Sanitize(big);

        clean.Count.Should().BeLessOrEqualTo(AuditMeta.MaxKeys);
        clean.Values.Should().OnlyContain(v => v.Length <= AuditMeta.MaxValueLength);
        clean.Keys.Should().NotContain(k => k.StartsWith('$') || k.Contains('.'));
    }

    [Fact]
    public void Events_are_sanitised_on_creation_not_by_each_caller()
    {
        var e = AuditEvent.Create(AuditActor.System("t"), "campaign.send", "campaign", "c1", AuditResults.Ok,
            new Dictionary<string, string> { ["recipients"] = "a@x.test;b@x.test", ["recipientCount"] = "2" });

        e.Meta.Should().NotContainKey("recipients");
        e.Meta.Should().ContainKey("recipientCount");
    }

    /* ── results ──────────────────────────────────────────────────────── */

    [Theory]
    [InlineData(200, "ok")]
    [InlineData(204, "ok")]
    [InlineData(302, "ok")]
    [InlineData(401, "denied")]
    [InlineData(403, "denied")]
    [InlineData(400, "failed")]
    [InlineData(404, "failed")]
    [InlineData(500, "failed")]
    public void The_status_decides_the_result(int status, string result) =>
        AuditResults.ForStatus(status).Should().Be(result);

    /* ── who did it ───────────────────────────────────────────────────── */

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer"));

    [Fact]
    public void Anonymous_callers_are_anonymous() =>
        AuditActorResolver.Resolve(new ClaimsPrincipal(new ClaimsIdentity()), null)
            .Should().Be(AuditActor.Anonymous);

    [Fact]
    public void A_pharmacy_user_acts_as_their_own_pharmacy()
    {
        var a = AuditActorResolver.Resolve(Principal(
            ("sub", "p1"), ("email", "shop@x.test"), (ClaimTypes.Role, "PharmacyUser")), null);

        a.Kind.Should().Be(ActorKinds.Pharmacy);
        a.Id.Should().Be("p1");
        a.PharmacyId.Should().Be("p1");
    }

    [Fact]
    public void Staff_outside_any_pharmacy_act_for_no_tenant()
    {
        var a = AuditActorResolver.Resolve(Principal(
            ("sub", "a1"), ("email", "nora@x.test"), (ClaimTypes.Role, "Admin")), null);

        a.Kind.Should().Be(ActorKinds.Admin);
        a.PharmacyId.Should().BeNull();
    }

    [Fact]
    public void Manage_as_is_recorded_as_the_admin_with_the_target_pharmacy()
    {
        // A delegated token: the subject is the shop, the actor is the person.
        var a = AuditActorResolver.Resolve(Principal(
            ("sub", "shop-9"), (ClaimTypes.Role, "PharmacyUser"),
            (DelegationClaims.Actor, "admin-1"),
            (DelegationClaims.ActorEmail, "nora@x.test"),
            (DelegationClaims.ActorRole, "Admin")), null);

        a.Id.Should().Be("admin-1", "the person who did it, not the account they were in");
        a.Kind.Should().Be(ActorKinds.Admin);
        a.PharmacyId.Should().Be("shop-9");
        a.Email.Should().Be("nora@x.test");
    }

    [Fact]
    public void A_head_office_working_as_its_branch_is_pharmacy_work()
    {
        var a = AuditActorResolver.Resolve(Principal(
            ("sub", "branch-2"), (ClaimTypes.Role, "PharmacyUser"),
            (DelegationClaims.Actor, "head-1"),
            (DelegationClaims.ActorRole, "PharmacyUser")), null);

        a.Kind.Should().Be(ActorKinds.Pharmacy);
        a.Id.Should().Be("head-1");
        a.PharmacyId.Should().Be("branch-2");
    }

    [Fact]
    public void The_smoke_account_is_a_machine()
    {
        var a = AuditActorResolver.Resolve(Principal(
            ("sub", "s1"), ("email", "SMOKE@sonar.local"), (ClaimTypes.Role, "PharmacyUser")),
            "smoke@sonar.local");

        a.Kind.Should().Be(ActorKinds.System);
    }

    [Fact]
    public void Seeders_are_system_actors() =>
        AuditActor.System("template-seeder").Should().BeEquivalentTo(
            new { Id = "system:template-seeder", Kind = ActorKinds.System, PharmacyId = (string?)null });

    [Theory]
    [InlineData(UserRole.PharmacyUser, "pharmacy", "u1")]
    [InlineData(UserRole.Admin, "admin", null)]
    [InlineData(UserRole.SuperAdmin, "admin", null)]
    public void Sign_in_attribution_follows_the_role(UserRole role, string kind, string? pharmacy)
    {
        var a = AuditActor.ForUser("u1", "u@x.test", role);
        a.Kind.Should().Be(kind);
        a.PharmacyId.Should().Be(pharmacy);
    }

    /* ── legacy rows ──────────────────────────────────────────────────── */

    [Fact]
    public void Every_legacy_action_has_a_real_name()
    {
        foreach (var action in Enum.GetValues<AdminAction>())
            LegacyAuditBackfill.ActionName(action, staffActor: true)
                .Should().NotStartWith("admin.legacy.", $"{action} needs a mapping");
    }

    [Fact]
    public void Legacy_acting_as_is_impersonation_only_for_staff()
    {
        LegacyAuditBackfill.ActionName(AdminAction.StartActingAs, staffActor: true)
            .Should().Be("admin.impersonate.start");
        LegacyAuditBackfill.ActionName(AdminAction.StartActingAs, staffActor: false)
            .Should().Be("auth.act_as.start");
    }

    /* ── CSV ──────────────────────────────────────────────────────────── */

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("plain", "plain")]
    [InlineData(null, "")]
    public void Csv_cells_cannot_run_as_formulas(string? value, string expected) =>
        AuditEventService.Cell(value).Should().Be(expected);

    /* ── who sees what ────────────────────────────────────────────────── */

    private static AuditEventService Service(params string[] managedPharmacyIds) =>
        new(StubStore.Instance, UsersStub.Managing(managedPharmacyIds));

    [Fact]
    public async Task A_superadmin_sees_every_tenant()
    {
        var built = await Service().BuildAsync(
            new AdminContext("sa", "sa@x.test", UserRole.SuperAdmin), new AuditEventSearch(AuditTab.Security), default);

        built!.Value.Query.VisiblePharmacyIds.Should().BeNull();
        built.Value.Query.VisibleActorId.Should().BeNull();
    }

    [Fact]
    public async Task An_admin_sees_linked_pharmacies_and_their_own_actions_only()
    {
        var built = await Service("p1", "p2").BuildAsync(
            new AdminContext("a1", "a@x.test", UserRole.Admin),
            // A pharmacy filter in the request narrows; it cannot widen.
            new AuditEventSearch(AuditTab.Pharmacy, PharmacyId: "someone-else"), default);

        built!.Value.Query.VisiblePharmacyIds.Should().BeEquivalentTo(new[] { "p1", "p2" });
        built.Value.Query.VisibleActorId.Should().Be("a1");
    }

    [Fact]
    public async Task A_pharmacy_user_gets_nothing()
    {
        var built = await Service().BuildAsync(
            new AdminContext("p", "p@x.test", UserRole.PharmacyUser), new AuditEventSearch(AuditTab.Security), default);

        built.Should().BeNull();
    }

    [Fact]
    public async Task The_security_tab_leaves_machines_out_unless_asked()
    {
        var sa = new AdminContext("sa", "sa@x.test", UserRole.SuperAdmin);

        var byDefault = await Service().BuildAsync(sa, new AuditEventSearch(AuditTab.Security), default);
        byDefault!.Value.Query.SecurityDefault.Should().BeTrue();
        byDefault.Value.Query.ActorKinds.Should().NotContain(ActorKinds.System);

        var system = await Service().BuildAsync(sa, new AuditEventSearch(AuditTab.Security, View: SecurityView.System), default);
        system!.Value.Query.ActorKinds.Should().Equal(ActorKinds.System);
    }

    [Theory]
    [InlineData(AuditTab.Security, AuditStreamView.Views, AuditStream.Audit)]
    [InlineData(AuditTab.Staff, AuditStreamView.Changes, AuditStream.Audit)]
    [InlineData(AuditTab.Staff, AuditStreamView.Views, AuditStream.Activity)]
    [InlineData(AuditTab.Pharmacy, AuditStreamView.Views, AuditStream.Activity)]
    public async Task Views_and_changes_come_from_separate_collections(
        AuditTab tab, AuditStreamView stream, AuditStream expected)
    {
        var built = await Service().BuildAsync(
            new AdminContext("sa", "sa@x.test", UserRole.SuperAdmin), new AuditEventSearch(tab, stream), default);

        built!.Value.Stream.Should().Be(expected);
    }

    [Fact]
    public async Task Staff_and_pharmacy_tabs_never_share_rows()
    {
        var sa = new AdminContext("sa", "sa@x.test", UserRole.SuperAdmin);
        var staff = await Service().BuildAsync(sa, new AuditEventSearch(AuditTab.Staff), default);
        var shop = await Service().BuildAsync(sa, new AuditEventSearch(AuditTab.Pharmacy), default);

        staff!.Value.Query.ActorKinds.Should().Equal(ActorKinds.Admin);
        shop!.Value.Query.ActorKinds.Should().Equal(ActorKinds.Pharmacy);
    }

    /* ── stubs ────────────────────────────────────────────────────────── */

    private sealed class StubStore : IAuditEventStore
    {
        public static readonly StubStore Instance = new();
        public Task InsertAsync(AuditStream s, AuditEvent e, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> InsertIfMissingAsync(AuditStream s, AuditEvent e, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> ExistsSinceAsync(
            AuditStream s, string a, string act, string? id, string? pharmacy, DateTime since, CancellationToken ct) =>
            Task.FromResult(false);
        public Task<AuditEventPage> SearchAsync(AuditStream s, AuditEventQuery q, CancellationToken ct) =>
            Task.FromResult(new AuditEventPage([], 0));
    }

    /// <summary>Answers ListManagedByAsync; any other call is a test bug.</summary>
    public class UsersStub : DispatchProxy
    {
        private IReadOnlyList<User> _managed = [];

        public static IUserRepository Managing(string[] ids)
        {
            var proxy = Create<IUserRepository, UsersStub>();
            ((UsersStub)(object)proxy)._managed = ids.Select(Pharmacy).ToList();
            return proxy;
        }

        private static User Pharmacy(string id)
        {
            var u = User.Register(
                Email.Create($"{id}@example.test"), id,
                PasswordHash.FromHash("x"));
            typeof(Acme.Domain.Common.Entity).GetProperty("Id")!.SetValue(u, id);
            return u;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == nameof(IUserRepository.ListManagedByAsync)
                ? Task.FromResult(_managed)
                : throw new NotSupportedException(method?.Name);
    }
}
