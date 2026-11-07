using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Auth;
using Acme.Domain.Common;
using Acme.Domain.Users;
using Acme.Infrastructure.Options;
using Acme.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acme.Tests.Hierarchy;

/// <summary>
/// Parent/child pharmacies and the delegated "manage as" token.
/// </summary>
/// <remarks>
/// The delegated token is the only thing in the system that lets one account
/// act as another, so the tests below are deliberately about what it must
/// <em>not</em> carry. A bug here does not surface as an error — it surfaces as
/// one pharmacy quietly holding another's privileges — so each rule gets its
/// own test rather than being folded into a single happy-path assertion.
/// </remarks>
public class PharmacyHierarchyTests
{
    private static User Pharmacy(string name = "Branch") =>
        User.Register(
            Email.Create($"{Guid.NewGuid():N}@example.com"),
            name,
            PasswordHash.FromHash("hash"));

    private static User Staff(UserRole role) =>
        User.CreateStaff(
            Email.Create($"{Guid.NewGuid():N}@example.com"),
            "Staff",
            PasswordHash.FromHash("hash"),
            role);

    private static JwtTokenService TokenService() =>
        // OptionsWrapper rather than Options.Create: `Acme.Infrastructure.Options`
        // is also in scope here, and a bare `Options.` reads ambiguously.
        new(new OptionsWrapper<JwtOptions>(new JwtOptions
        {
            // Long enough for HMAC-SHA256; the value itself is irrelevant here.
            Secret = "test-secret-that-is-long-enough-for-hmac-sha256-signing",
            Issuer = "acme-tests",
            Audience = "acme-tests",
            ExpiryMinutes = 60,
        }));

    private static JwtSecurityToken Decode(string token) =>
        new JwtSecurityTokenHandler().ReadJwtToken(token);

    // ── domain ────────────────────────────────────────────────────────────

    [Fact]
    public void A_pharmacy_placed_under_a_parent_reports_itself_as_a_child()
    {
        var child = Pharmacy();
        child.IsChildPharmacy.Should().BeFalse();

        child.SetParent("parent-id");

        child.ParentPharmacyId.Should().Be("parent-id");
        child.IsChildPharmacy.Should().BeTrue();
    }

    [Fact]
    public void A_pharmacy_cannot_be_its_own_parent()
    {
        // Guards the shape of cycle that a single account can see: pointing at
        // itself. Longer cycles are prevented by refusing chains entirely.
        var user = Pharmacy();

        var act = () => user.SetParent(user.Id);

        act.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.SuperAdmin)]
    public void Staff_accounts_cannot_be_placed_under_a_pharmacy(UserRole role)
    {
        // Otherwise a pharmacy could be handed an admin as a "branch" and then
        // manage-as into it, which is a straight privilege escalation.
        var staff = Staff(role);

        var act = () => staff.SetParent("parent-id");

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Detaching_leaves_the_account_intact_and_able_to_sign_in()
    {
        // The product promise: removing a branch is not a deletion.
        var child = Pharmacy();
        child.SetParent("parent-id");

        child.DetachFromParent();

        child.ParentPharmacyId.Should().BeNull();
        child.IsChildPharmacy.Should().BeFalse();
        child.CanSignIn.Should().BeTrue();
    }

    [Fact]
    public void An_invited_pharmacy_cannot_sign_in_until_it_activates()
    {
        // A parent may create a branch that nobody has claimed yet. Until the
        // invitee sets a password there is no account to manage.
        var invited = User.Invite(
            Email.Create("new@example.com"), "New Branch", PasswordHash.FromHash("unusable"));

        invited.CanSignIn.Should().BeFalse();

        invited.Activate(PasswordHash.FromHash("real-hash"));

        invited.CanSignIn.Should().BeTrue();
    }

    // ── delegated token ───────────────────────────────────────────────────

    [Fact]
    public void The_delegated_token_is_issued_for_the_managed_pharmacy_not_the_parent()
    {
        // Every endpoint reads `sub` to decide whose data it is touching. If
        // this were the parent's id, "manage as" would show the parent their
        // own data while claiming otherwise.
        var parent = Pharmacy("Parent");
        var child = Pharmacy("Child");

        var (token, _) = TokenService().IssueDelegated(child, parent, TimeSpan.FromMinutes(30));

        var jwt = Decode(token);
        jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Sub).Value
            .Should().Be(child.Id);
    }

    [Fact]
    public void The_delegated_token_carries_the_managed_accounts_role_not_the_actors()
    {
        // The escalation case. An admin managing a pharmacy must hold pharmacy
        // privileges for the duration, not admin ones.
        var actor = Staff(UserRole.SuperAdmin);
        var child = Pharmacy("Child");

        var (token, _) = TokenService().IssueDelegated(child, actor, TimeSpan.FromMinutes(30));

        var roles = Decode(token).Claims
            .Where(c => c.Type is ClaimTypes.Role or "role")
            .Select(c => c.Value)
            .ToList();

        roles.Should().NotBeEmpty();
        roles.Should().OnlyContain(r => r == UserRole.PharmacyUser.ToString());
        roles.Should().NotContain(UserRole.SuperAdmin.ToString());
    }

    [Fact]
    public void The_delegated_token_records_who_is_really_acting()
    {
        // This claim is what the audit log attributes writes to, and what the
        // API reads to refuse further delegation. Without it a delegated token
        // is indistinguishable from an ordinary sign-in.
        var parent = Pharmacy("Parent");
        var child = Pharmacy("Child");

        var (token, _) = TokenService().IssueDelegated(child, parent, TimeSpan.FromMinutes(30));

        var claims = Decode(token).Claims.ToList();
        claims.Single(c => c.Type == DelegationClaims.Actor).Value.Should().Be(parent.Id);
        claims.Single(c => c.Type == DelegationClaims.ActorEmail).Value
            .Should().Be(parent.Email.Value);
    }

    [Fact]
    public void A_delegated_token_never_lasts_longer_than_an_hour()
    {
        // The lifetime is capped at the point of issue rather than validated at
        // the call sites, so no future caller can widen it by passing a larger
        // window.
        var parent = Pharmacy("Parent");
        var child = Pharmacy("Child");

        var (_, expiresAt) = TokenService()
            .IssueDelegated(child, parent, TimeSpan.FromDays(30));

        expiresAt.Should().BeBefore(DateTime.UtcNow.AddHours(1).AddMinutes(1));
    }

    [Fact]
    public void An_ordinary_token_is_not_marked_as_delegated()
    {
        // The inverse of the rule above: absence of the actor claim is what
        // makes a normal session normal, so it must genuinely be absent.
        var user = Pharmacy();

        var (token, _) = TokenService().Issue(user);

        Decode(token).Claims.Should().NotContain(c => c.Type == DelegationClaims.Actor);
    }
}
