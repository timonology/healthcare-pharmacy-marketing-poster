using Acme.Application.Social;
using Acme.WebApi.Auditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

/// <summary>
/// The two callbacks Meta calls when a person removes our app or asks for
/// their data to be deleted.
/// </summary>
/// <remarks>
/// <para>
/// Register both in the Meta app (Facebook Login → Settings, and App settings
/// → Basic → Data deletion), on the <b>API's</b> public address:
/// <c>https://&lt;api&gt;/api/social/meta/deauthorize</c> and
/// <c>https://&lt;api&gt;/api/social/meta/data-deletion</c>.
/// </para>
/// <para>
/// Anonymous by necessity, like the Stripe webhook: Meta has no session with
/// us. The <c>signed_request</c> is the credential — HMAC-SHA256 under our app
/// secret, verified before anything is read — and an unsigned or wrongly
/// signed request gets a 400 and changes nothing. What a valid one can do is
/// bounded to the connections the signing person granted.
/// </para>
/// </remarks>
[ApiController]
[Route("api/social/meta")]
[AllowAnonymous]
public sealed class MetaCallbacksController(
    IMetaSignedRequestReader signedRequests,
    MetaCallbackService callbacks) : ControllerBase
{
    [Audit("social.meta.deauthorize", "social_connection", SystemActor = "meta")]
    [HttpPost("deauthorize")]
    [Consumes("application/x-www-form-urlencoded")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Deauthorize(
        [FromForm(Name = "signed_request")] string? signedRequest, CancellationToken ct)
    {
        if (signedRequests.ReadUserId(signedRequest) is not { } userId) return BadRequest();

        await callbacks.DeauthorizeAsync(userId, ct);
        return Ok();
    }

    /// <summary>
    /// Deletes what we hold for the person and answers in the shape Meta
    /// requires: a status URL and a confirmation code.
    /// </summary>
    [Audit("social.meta.data_deletion", "social_connection", SystemActor = "meta")]
    [HttpPost("data-deletion")]
    [Consumes("application/x-www-form-urlencoded")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DataDeletion(
        [FromForm(Name = "signed_request")] string? signedRequest, CancellationToken ct)
    {
        if (signedRequests.ReadUserId(signedRequest) is not { } userId) return BadRequest();

        var result = await callbacks.DeleteDataAsync(userId, ct);

        // Meta's field names, exactly — not the API's usual camelCase.
        return Ok(new Dictionary<string, string>
        {
            ["url"] = result.Url,
            ["confirmation_code"] = result.ConfirmationCode,
        });
    }
}
