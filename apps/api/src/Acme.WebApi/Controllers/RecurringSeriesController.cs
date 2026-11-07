using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Common;
using Acme.Application.Social;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Recurring social posts: one poster or video, posted to the same accounts
/// on a schedule until paused or ended.
/// </summary>
/// <remarks>
/// Scoped to the caller's own id from the token, like everything under
/// <c>/api/social</c>. The plan limit is enforced by the service on create
/// and resume, not only in the form.
/// </remarks>
[ApiController]
[Route("api/social/series")]
[Authorize]
public sealed class RecurringSeriesController(RecurringSeriesService series) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(RecurringSeriesListDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await series.ListAsync(userId, ct));
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(RecurringSeriesDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await series.GetAsync(userId, id, ct));
    }

    /// <summary>The dates a rule would post on. Read-only: nothing is saved.</summary>
    [NoAudit("Read-only: works out the dates a rule would post on; nothing is saved.")]
    [HttpPost("preview")]
    [ProducesResponseType(typeof(SeriesPreviewDto), StatusCodes.Status200OK)]
    public IActionResult Preview([FromBody] SeriesPreviewRequest body)
    {
        if (CurrentUserId() is null) return Unauthorized();
        if (body is null) return BadRequest(new { error = "Choose how often to post." });
        return Respond(series.Preview(body, DateTime.UtcNow));
    }

    [Audit("social.series.create", "social_series")]
    [HttpPost]
    [ProducesResponseType(typeof(RecurringSeriesDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] SeriesRequest body, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        if (body is null) return BadRequest(new { error = "Choose a poster or video." });
        return Respond(await series.CreateAsync(userId, body, ct));
    }

    [Audit("social.series.update", "social_series")]
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(RecurringSeriesDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(string id, [FromBody] SeriesRequest body, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        if (body is null) return BadRequest(new { error = "Nothing to change." });
        return Respond(await series.UpdateAsync(userId, id, body, ct));
    }

    [Audit("social.series.pause", "social_series")]
    [HttpPost("{id}/pause")]
    public async Task<IActionResult> Pause(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await series.PauseAsync(userId, id, ct));
    }

    [Audit("social.series.resume", "social_series")]
    [HttpPost("{id}/resume")]
    public async Task<IActionResult> Resume(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await series.ResumeAsync(userId, id, ct));
    }

    [Audit("social.series.end", "social_series")]
    [HttpPost("{id}/end")]
    public async Task<IActionResult> End(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await series.EndAsync(userId, id, ct));
    }

    private string? CurrentUserId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

    private IActionResult Respond<T>(Result<T> result) => result.Kind switch
    {
        ResultKind.Success => Ok(result.Value),
        ResultKind.NotFound => NotFound(new { error = result.Error }),
        ResultKind.Invalid => BadRequest(new { error = result.Error }),
        ResultKind.Conflict => Conflict(new { error = result.Error }),
        ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
        ResultKind.Unauthorized => Unauthorized(new { error = result.Error }),
        _ => BadRequest(new { error = result.Error }),
    };
}
