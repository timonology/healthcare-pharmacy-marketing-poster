using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Analytics;
using Acme.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/analytics")]
[Authorize]
public sealed class AnalyticsController(AnalyticsService service) : ControllerBase
{
    /// <summary>
    /// Returns the analytics overview for the current user. 403 if the user's
    /// tier doesn't include the analytics dashboard (Free/Starter today, Pro
    /// only). Frontend uses this to gate the /analytics page.
    /// </summary>
    [ActivityLog("dashboard.view", DedupeSeconds = 3600)]
    [HttpGet("overview")]
    [ProducesResponseType(typeof(AnalyticsOverviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.GetOverviewAsync(userId, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    private string? CurrentUserId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
}
