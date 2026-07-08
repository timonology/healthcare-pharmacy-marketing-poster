using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Application.Profile;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/me")]
[Authorize]
public sealed class MeController(
    IUserRepository users,
    ProfileService profile) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(UserProfile), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var sub = CurrentUserId();
        if (sub is null) return Unauthorized();

        var user = await users.FindByIdAsync(sub, ct);
        if (user is null) return NotFound();

        return Ok(new UserProfile(user.Id, user.Email.Value, user.DisplayName, user.Tier, user.Profile.OnboardingCompleted, user.CreatedAtUtc));
    }

    [HttpGet("full")]
    [ProducesResponseType(typeof(MeDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFull(CancellationToken ct)
    {
        var sub = CurrentUserId();
        if (sub is null) return Unauthorized();

        var result = await profile.GetMeAsync(sub, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [HttpPut("profile")]
    [ProducesResponseType(typeof(MeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateProfile(
        [FromBody] UpsertProfileRequest request,
        CancellationToken ct)
    {
        var sub = CurrentUserId();
        if (sub is null) return Unauthorized();

        var result = await profile.UpdateProfileAsync(sub, request, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            ResultKind.Invalid => BadRequest(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [HttpPost("onboard")]
    [ProducesResponseType(typeof(MeDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Onboard(
        [FromBody] OnboardRequest request,
        CancellationToken ct)
    {
        var sub = CurrentUserId();
        if (sub is null) return Unauthorized();

        var result = await profile.OnboardAsync(sub, request, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            ResultKind.Invalid => BadRequest(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    private string? CurrentUserId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
}
