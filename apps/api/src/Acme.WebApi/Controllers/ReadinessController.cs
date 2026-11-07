using System.Security.Cryptography;
using System.Text;
using Acme.Infrastructure.Readiness;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Is every dependency of this deployment working? For <c>pnpm smoke</c> and
/// for a person deploying.
/// </summary>
/// <remarks>
/// <para>
/// <b>Protected by a shared token, not a user account.</b> The smoke test runs
/// as an ordinary pharmacy on purpose (least privilege), and a deploy pipeline
/// has no account at all. The report names which settings are missing and
/// which providers refuse — never a value — but even names and verdicts are a
/// map of the infrastructure, so it is not anonymous: the caller must present
/// <c>Readiness:Token</c> in <c>X-Readiness-Token</c>.
/// </para>
/// <para>
/// <b>404 when no token is configured</b>, so a deployment that never opted in
/// has no such endpoint to probe. Compared in constant time.
/// </para>
/// </remarks>
[ApiController]
[Route("api/health/ready")]
[AllowAnonymous]
public sealed class ReadinessController(ReadinessService readiness, IConfiguration config) : ControllerBase
{
    public const string TokenHeader = "X-Readiness-Token";

    [HttpGet]
    [ProducesResponseType(typeof(ReadinessReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var expected = config["Readiness:Token"];
        if (string.IsNullOrWhiteSpace(expected) || expected.Length < 24) return NotFound();

        var presented = Request.Headers[TokenHeader].ToString();
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
                SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
            return NotFound();

        // Never cached: a stale "ok" is worse than none.
        Response.Headers.CacheControl = "no-store";
        return Ok(await readiness.RunAsync(ct));
    }
}
