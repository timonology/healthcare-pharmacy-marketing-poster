using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Auth;
using Acme.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Getting a credential: register, sign in, refresh, sign out.
/// </summary>
/// <remarks>
/// <para>
/// The three ways of obtaining a session are marked anonymous individually.
/// <see cref="Logout"/> is not: it keeps its <c>[Authorize]</c>, because ending
/// a session is something only the holder of one should be doing.
/// </para>
/// <para>
/// Marked per action rather than on the class, and that distinction is not
/// cosmetic. An earlier version put <c>[AllowAnonymous]</c> on the controller
/// to make the intent explicit — but <b>[AllowAnonymous] overrides a
/// method-level [Authorize]</b> in ASP.NET Core, so it silently unprotected
/// <see cref="Logout"/>. The endpoint still answered 401 from its own body, so
/// nothing looked different; the pipeline had simply stopped being the thing
/// enforcing it.
/// </para>
/// </remarks>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService auth, AccountEmailService accountEmail) : ControllerBase
{
    /// <summary>Register a new user. Returns access + refresh tokens.</summary>
    [AllowAnonymous]
    [Audit("auth.register", "user", FailedAction = "auth.register.failed")]
    [EnableRateLimiting(RateLimits.Auth)]
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
    [AllowAnonymous]
    [Audit("auth.login", "user", FailedAction = "auth.login.failed")]
    [EnableRateLimiting(RateLimits.Auth)]
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await auth.LoginAsync(
            request, ct, HttpContext.Connection.RemoteIpAddress?.ToString());
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
    [AllowAnonymous]
    [NoAudit("Token refresh runs every few minutes per session; sign-in and sign-out are the security events.")]
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

    /// <summary>
    /// Start a password reset. Always answers 202, whatever the address.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The uniform response is the point.</b> An endpoint that distinguishes
    /// "sent" from "no such account" is an account-enumeration oracle: feed it
    /// a breach dump and it sorts the list into customers and non-customers,
    /// which is precisely what a credential-stuffing run needs. So it accepts
    /// the request and says nothing about what happened next.
    /// </para>
    /// <para>
    /// 202 rather than 200 for the same reason — it means "accepted", which is
    /// all that is truthfully being claimed.
    /// </para>
    /// </remarks>
    [AllowAnonymous]
    [Audit("auth.password_reset.request", "user")]
    [EnableRateLimiting(RateLimits.AccountEmail)]
    [HttpPost("forgot-password")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ForgotPassword(
        [FromBody] ForgotPasswordRequest request, CancellationToken ct)
    {
        await accountEmail.RequestPasswordResetAsync(request.Email ?? string.Empty, ct);

        return Accepted(new
        {
            message = "If that address has an account, a reset link is on its way.",
        });
    }

    /// <summary>Set a new password using the token from the emailed link.</summary>
    /// <remarks>
    /// Succeeding here revokes every session on the account — see
    /// <c>AccountEmailService.ResetPasswordAsync</c> for why.
    /// </remarks>
    [AllowAnonymous]
    [Audit("auth.password_reset.complete", "user", FailedAction = "auth.password_reset.failed")]
    [EnableRateLimiting(RateLimits.Auth)]
    [HttpPost("reset-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword(
        [FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        var result = await accountEmail.ResetPasswordAsync(
            request.Token ?? string.Empty, request.Password ?? string.Empty, ct);

        return result.Kind switch
        {
            ResultKind.Success => NoContent(),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>Confirm an email address using the token from the emailed link.</summary>
    [AllowAnonymous]
    [Audit("auth.email.verify", "user", FailedAction = "auth.email.verify_failed")]
    [EnableRateLimiting(RateLimits.Auth)]
    [HttpPost("verify-email")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> VerifyEmail(
        [FromBody] VerifyEmailRequest request, CancellationToken ct)
    {
        var result = await accountEmail.VerifyEmailAsync(request.Token ?? string.Empty, ct);

        return result.Kind switch
        {
            ResultKind.Success => NoContent(),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>Send a fresh verification email to the signed-in account.</summary>
    /// <remarks>
    /// Authenticated, unlike the other three: this one is for somebody who is
    /// already in and never confirmed their address, so there is a session to
    /// take the identity from and no address to enumerate.
    /// </remarks>
    [Authorize]
    [Audit("auth.email.resend_verification", "user")]
    [EnableRateLimiting(RateLimits.AccountEmail)]
    [HttpPost("resend-verification")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ResendVerification(CancellationToken ct)
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(sub)) return Unauthorized();

        await accountEmail.ResendVerificationAsync(sub, ct);
        return Accepted(new { message = "If the address still needs confirming, a link is on its way." });
    }

    /// <summary>
    /// What happened to the last reset or verification email sent to the
    /// signed-in account.
    /// </summary>
    /// <remarks>
    /// Signed-in only and about your own account, so it reveals nothing the
    /// anonymous request hides. The smoke test requests a reset for its own
    /// account through the web app and then asks here whether the provider
    /// accepted it — which is the check that would have caught "reset emails
    /// are not arriving" before anybody had to try it on production.
    /// </remarks>
    [Authorize]
    [HttpGet("account-email/latest")]
    [ProducesResponseType(typeof(AccountEmailStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> LatestAccountEmail(
        [FromQuery] string purpose = "PasswordReset", CancellationToken ct = default)
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(sub)) return Unauthorized();

        if (!Enum.TryParse<Acme.Domain.Users.AuthTokenPurpose>(purpose, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
            return BadRequest(new { error = "purpose is PasswordReset or EmailVerification." });

        var status = await accountEmail.LatestAsync(sub, parsed, ct);
        return status is null ? NoContent() : Ok(status);
    }

    /// <summary>Revoke every refresh token belonging to the current user.</summary>
    [Audit("auth.logout", "user")]
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
