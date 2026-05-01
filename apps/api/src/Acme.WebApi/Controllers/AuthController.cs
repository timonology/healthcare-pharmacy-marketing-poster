using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Auth;
using Acme.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    /// <summary>Register a new user. Returns access + refresh tokens.</summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var result = await auth.RegisterAsync(request, ct);
        return result.Kind switch
        {
            ResultKind.Success => StatusCode(StatusCodes.Status201Created, result.Value),
            ResultKind.Conflict => Conflict(new { error = result.Error }),
            ResultKind.Invalid => BadRequest(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>Authenticate with email + password. Returns access + refresh tokens.</summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await auth.LoginAsync(request, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.Unauthorized => Unauthorized(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>
    /// Exchange a refresh token for a new access + refresh pair (rotating).
    /// Old refresh token is revoked.
    /// </summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
    {
        var result = await auth.RefreshAsync(request.RefreshToken, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.Unauthorized => Unauthorized(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>Revoke every refresh token belonging to the current user.</summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(sub)) return Unauthorized();

        await auth.LogoutAsync(sub, ct);
        return NoContent();
    }
}
