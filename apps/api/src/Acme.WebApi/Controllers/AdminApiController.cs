using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Admin;
using Acme.Application.Common;
using Acme.Domain.Users;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Shared plumbing for controllers under <c>/api/admin</c>.
/// </summary>
/// <remarks>
/// Two things live here, and both are the kind of thing that must not be
/// reimplemented per controller.
///
/// <c>Context()</c> builds the caller's identity from validated JWT claims and
/// nothing else. A second copy of it that read the role from, say, a header
/// would be an authentication bypass, and the way that gets written is by
/// someone adding a controller and copying "most of" this method.
///
/// <c>Respond()</c> maps a <see cref="Result{T}"/> onto status codes. A
/// controller that mapped Forbidden to 400 would turn a refusal the UI can
/// explain into a generic failure, so the mapping is defined once.
/// </remarks>
[ApiController]
public abstract class AdminApiController : ControllerBase
{
    /// <summary>
    /// The authenticated caller, or null when a required claim is missing.
    /// </summary>
    /// <remarks>
    /// Returns null rather than a default role on a missing or unparseable
    /// role claim. A token we cannot fully interpret is treated as no token —
    /// the alternative, falling back to some role, is how an unparseable claim
    /// becomes an access grant.
    /// </remarks>
    protected AdminContext? Context()
    {
        var id = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                 ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(id)) return null;

        var email = User.FindFirstValue(JwtRegisteredClaimNames.Email)
                    ?? User.FindFirstValue(ClaimTypes.Email)
                    ?? "(unknown)";

        var roleName = User.FindFirstValue(ClaimTypes.Role);
        if (!Enum.TryParse<UserRole>(roleName, out var role)) return null;

        return new AdminContext(id, email, role);
    }

    protected IActionResult Respond<T>(Result<T> result) => result.Kind switch
    {
        ResultKind.Success => Ok(result.Value),
        ResultKind.NotFound => NotFound(new { error = result.Error }),
        ResultKind.Invalid => BadRequest(new { error = result.Error }),
        ResultKind.Conflict => Conflict(new { error = result.Error }),
        // 403 with a body, not the framework's empty Forbid(), so the UI can
        // explain *why* an action was refused.
        ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
        ResultKind.Unauthorized => Unauthorized(new { error = result.Error }),
        _ => BadRequest(new { error = result.Error }),
    };
}
