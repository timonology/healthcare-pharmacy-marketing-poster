using Acme.Application.Common;
using Acme.Application.Patients;
using Acme.WebApi.Auditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Unsubscribing from a pharmacy's campaign emails (security review R2-06).
/// </summary>
/// <remarks>
/// <para>
/// Anonymous: the person has no account. The signed token in the link is the
/// authority, and all it can do is stop mail to its own address.
/// </para>
/// <para>
/// GET only describes; it never unsubscribes. Mail scanners and link
/// previewers fetch every link in a message, so a GET that acted would
/// unsubscribe people who never clicked. The web page asks for a button press
/// and then POSTs. Mail clients POST here directly for their own one-click
/// "Unsubscribe" button (RFC 8058), with a form body this endpoint ignores.
/// </para>
/// <para>
/// No rate limit: one-click requests arrive from the mail providers' servers,
/// so a busy morning is many people behind few addresses, and the token
/// already cannot be guessed.
/// </para>
/// </remarks>
[ApiController]
[Route("api/unsubscribe")]
[AllowAnonymous]
public sealed class UnsubscribeController(UnsubscribeService unsubscribe) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(UnsubscribePreview), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Preview([FromQuery] string? token, CancellationToken ct) =>
        Respond(await unsubscribe.PreviewAsync(token, ct));

    [HttpPost]
    [Audit("patient.unsubscribe", "pharmacy")]
    [ProducesResponseType(typeof(UnsubscribePreview), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Unsubscribe([FromQuery] string? token, CancellationToken ct) =>
        Respond(await unsubscribe.UnsubscribeAsync(token, ct));

    private IActionResult Respond(Result<UnsubscribePreview> result) =>
        result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
}
