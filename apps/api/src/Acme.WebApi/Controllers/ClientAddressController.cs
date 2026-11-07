using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Whether the API can tell browsers apart (security review R2-03).
/// </summary>
/// <remarks>
/// <para>
/// Every browser request reaches the API through the web app, so unless the web
/// app passes the browser's address on — signed with the key both services
/// share — the API sees one address for everybody. The per-address limits on
/// sign-in, password reset and screen pairing then apply to all users at once:
/// five reset requests in fifteen minutes, from anyone, and every later one is
/// refused. The web app answers "a link is on its way" regardless, so from the
/// outside that is exactly "reset emails are not arriving".
/// </para>
/// <para>
/// Called through the web app (the smoke test does), this says which case the
/// deployment is in. Signed-in only, and about the caller's own request; the
/// address is truncated.
/// </para>
/// </remarks>
[ApiController]
[Route("api/diagnostics/client-address")]
[Authorize]
public sealed class ClientAddressController(IConfiguration config) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var keyConfigured = !string.IsNullOrWhiteSpace(config["Edge:ClientIpKey"]);
        var signature = HttpContext.Items[SignedClientIpMiddleware.SignatureItem] as string;

        var verdict = (keyConfigured, signature) switch
        {
            (true, SignedClientIpMiddleware.SignatureValid) => "ok",
            (true, SignedClientIpMiddleware.SignatureInvalid) => "key_mismatch",
            (true, _) => "web_not_signing",
            (false, _) => "api_key_missing",
        };

        return Ok(new
        {
            verdict,
            keyConfigured,
            signature = signature ?? "absent",
            address = RateLimits.Truncated(HttpContext.Connection.RemoteIpAddress),
        });
    }
}
