using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Acme.WebApi;

/// <summary>
/// Whether a request is somebody working as a pharmacy that is not their own.
/// </summary>
/// <remarks>
/// <para>
/// A delegated token carries the real person in <c>act</c> and the pharmacy
/// being worked as in <c>sub</c>. Three paths issue one: a head office managing
/// a branch, a member switching into a pharmacy they belong to, and platform
/// staff doing support.
/// </para>
/// <para>
/// <b>The presence of <c>act</c> alone is not the test.</b> Switching back to
/// your own pharmacy through the workspace switcher is a switch like any other,
/// so it issues a delegated token whose actor and subject are the same person.
/// Reading "has an actor" as "is someone else" would lock people out of their
/// own account's billing and branch tools after a perfectly ordinary switch.
/// </para>
/// </remarks>
public static class Delegation
{
    public static bool IsActingForAnother(this ClaimsPrincipal user)
    {
        var actor = user.FindFirstValue(DelegationClaims.Actor);
        if (string.IsNullOrEmpty(actor)) return false;

        var subject = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
                      ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return !string.Equals(actor, subject, StringComparison.Ordinal);
    }
}

/// <summary>
/// Refuses the action while the caller is working as another pharmacy.
/// </summary>
/// <remarks>
/// <para>
/// For actions only the account's own holder may take — chiefly money: a plan
/// checkout, a credit purchase, the billing portal (card, invoices,
/// cancellation). A branch is its own Stripe customer and often its own
/// business, and the audit log can say who cancelled a plan but cannot undo it.
/// Security review R3-06.
/// </para>
/// <para>
/// A filter rather than a line in each action so the rule is visible on the
/// endpoint and cannot be forgotten in one of several. It runs before the
/// action body, so nothing reaches Stripe. The refusal still
/// passes through the audit middleware, so an attempt is on record.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class OwnAccountOnlyAttribute : ActionFilterAttribute
{
    /// <summary>Machine-readable reason, so the web app can explain rather than just fail.</summary>
    public const string Code = "own_account_only";

    public const string Message =
        "Billing can only be changed by this pharmacy's own account holder. " +
        "Return to your own account to manage yours.";

    /// <summary>
    /// Ahead of [ApiController]'s automatic 400 (order -2000), so a delegated
    /// caller is told why they cannot do this rather than what was wrong with
    /// a body that was never going to be used.
    /// </summary>
    public OwnAccountOnlyAttribute() => Order = -3000;

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.HttpContext.User.IsActingForAnother()) return;

        context.Result = new ObjectResult(new { error = Message, code = Code })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }
}
