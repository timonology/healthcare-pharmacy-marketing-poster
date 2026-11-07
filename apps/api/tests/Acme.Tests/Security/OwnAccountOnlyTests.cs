using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using Acme.Application.Auth;
using Acme.WebApi;
using Acme.WebApi.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Acme.Tests.Security;

/// <summary>
/// Billing is for the pharmacy's own account holder (security review R3-06).
/// </summary>
/// <remarks>
/// Host-free: the rule itself, and a reflection sweep so a billing endpoint
/// added later is covered the moment it is written rather than when somebody
/// remembers. The end-to-end behaviour is in
/// <c>Acme.IntegrationTests.BillingDelegationTests</c>.
/// </remarks>
public class OwnAccountOnlyTests
{
    private static ClaimsPrincipal Token(string sub, string? actor = null)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, sub) };
        if (actor is not null) claims.Add(new Claim(DelegationClaims.Actor, actor));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void Your_own_ordinary_session_is_not_acting_for_another() =>
        Token("pharmacy-1").IsActingForAnother().Should().BeFalse();

    [Fact]
    public void Switching_home_is_not_acting_for_another()
    {
        // The workspace switcher issues a delegated token for your own pharmacy
        // too, with you as both actor and subject.
        Token("pharmacy-1", actor: "pharmacy-1").IsActingForAnother().Should().BeFalse();
    }

    [Fact]
    public void Working_as_a_branch_is_acting_for_another() =>
        Token("branch-1", actor: "head-office-1").IsActingForAnother().Should().BeTrue();

    [Fact]
    public void The_filter_refuses_someone_working_as_another_pharmacy_with_a_reason()
    {
        var context = Executing(Token("branch-1", actor: "head-office-1"));

        new OwnAccountOnlyAttribute().OnActionExecuting(context);

        var result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        result.Value!.GetType().GetProperty("code")!.GetValue(result.Value)
            .Should().Be(OwnAccountOnlyAttribute.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pharmacy-1")]
    public void The_filter_lets_the_account_holder_through(string? actor)
    {
        var context = Executing(Token("pharmacy-1", actor));

        new OwnAccountOnlyAttribute().OnActionExecuting(context);

        context.Result.Should().BeNull("the action runs");
    }

    [Fact]
    public void The_filter_runs_before_the_automatic_bad_request()
    {
        // [ApiController]'s model-state filter is order -2000. Running after it
        // would tell a delegated caller what was wrong with a body that was
        // never going to be used, instead of why they cannot do this at all.
        new OwnAccountOnlyAttribute().Order.Should().BeLessThan(-2000);
    }

    /// <summary>
    /// Every billing action that can start a payment, open the card portal or
    /// change a plan carries the rule. Only the Stripe webhook and the read-only
    /// status are exempt, and each exemption is named.
    /// </summary>
    [Theory]
    [InlineData(typeof(BillingController))]
    [InlineData(typeof(SubscriptionController))]
    public void Every_billing_change_is_for_the_account_holder_only(Type controller)
    {
        var exempt = new HashSet<string>
        {
            "BillingController.Webhook", // Stripe, authenticated by signature; no pharmacy session
        };

        var unguarded = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>()
                .Any(a => a.HttpMethods.Any(v => v is "POST" or "PUT" or "PATCH" or "DELETE")))
            .Where(m => !exempt.Contains($"{controller.Name}.{m.Name}"))
            .Where(m => m.GetCustomAttribute<OwnAccountOnlyAttribute>() is null)
            .Select(m => $"{controller.Name}.{m.Name}")
            .ToList();

        unguarded.Should().BeEmpty(
            "a head office working as a branch must not be able to change the branch's plan or card (R3-06)");
    }

    private static ActionExecutingContext Executing(ClaimsPrincipal user)
    {
        var http = new DefaultHttpContext { User = user };
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(action, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());
    }
}
