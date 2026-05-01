using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/me")]
[Authorize]
public sealed class MeController(IUserRepository users) : ControllerBase
{
    /// <summary>Returns the profile of the currently authenticated user.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(UserProfile), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(sub)) return Unauthorized();

        var user = await users.FindByIdAsync(sub, ct);
        if (user is null) return NotFound();

        return Ok(new UserProfile(user.Id, user.Email.Value, user.DisplayName, user.CreatedAtUtc));
    }
}
