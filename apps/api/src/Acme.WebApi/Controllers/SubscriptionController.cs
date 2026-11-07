using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Common;
using Acme.Application.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/subscription")]
[Authorize]
public sealed class SubscriptionController(SubscriptionService service) : ControllerBase
{
    [HttpGet("current")]
    [ProducesResponseType(typeof(CurrentSubscriptionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Current(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var result = await service.GetCurrentAsync(userId, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [HttpGet("plans")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IReadOnlyList<PlanDto>), StatusCodes.Status200OK)]
    public IActionResult Plans() => Ok(service.GetPlans());

    // Only reachable with Stripe off, but it still changes a plan.
    [OwnAccountOnly]
    [Audit("subscription.upgrade", "subscription")]
    [HttpPost("upgrade")]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(CurrentSubscriptionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upgrade(
        [FromBody] UpgradeRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var result = await service.UpgradeAsync(userId, request.Tier, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            ResultKind.Invalid => BadRequest(new { error = result.Error }),
            // Stripe is live — self-serve tier changes are disabled. 409 rather
            // than 403 so the client can distinguish "not allowed here" from
            // "not authenticated" and redirect to checkout.
            ResultKind.Forbidden => Conflict(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    private string? CurrentUserId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
}
