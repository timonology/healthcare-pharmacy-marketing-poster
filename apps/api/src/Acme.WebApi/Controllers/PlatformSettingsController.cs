using Acme.Application.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Platform-wide settings. SuperAdmin only.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="AdminController"/> and guarded by
/// <see cref="AuthPolicies.SuperAdminOnly"/> at the class level, not per
/// action. Everything here affects every pharmacy at once, so there is no
/// action on this controller an ordinary Admin should reach — making that a
/// class-level rule means a route added later inherits it rather than relying
/// on someone remembering the attribute.
/// </para>
/// <para>
/// <see cref="PlatformSettingsService"/> re-checks the role as well. The policy
/// answers "may this person enter"; the service answers "may this person change
/// something global", and defence in depth is cheap for a setting that can
/// change what every video costs.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/settings")]
[Authorize(Policy = AuthPolicies.SuperAdminOnly)]
public sealed class PlatformSettingsController(PlatformSettingsService settings) : AdminApiController
{
    /// <summary>The video model in force, where it came from, and the alternatives.</summary>
    [ActivityLog("admin.console.settings")]
    [HttpGet("video-model")]
    [ProducesResponseType(typeof(VideoModelSettingDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetVideoModel(CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        _ = actor;   // read-only, but the token still has to parse
        return Respond(await settings.GetVideoModelSettingAsync(ct));
    }

    /// <summary>
    /// Changes the model used for video generation.
    /// </summary>
    /// <remarks>
    /// Takes effect on the next video claimed by the worker — jobs already
    /// rendering keep the model they started with, because switching a model
    /// mid-pipeline would mix two models' output in one script.
    /// </remarks>
    [Audit("admin.settings.change", "setting")]
    [HttpPut("video-model")]
    [ProducesResponseType(typeof(VideoModelSettingDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetVideoModel(
        [FromBody] UpdateVideoModelRequest body, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        if (body is null) return BadRequest(new { error = "Choose a model." });

        return Respond(await settings.SetVideoModelAsync(actor, body.Model, ct));
    }

    /// <summary>Drops the override so the configured environment value applies again.</summary>
    [Audit("admin.settings.reset", "setting")]
    [HttpDelete("video-model")]
    [ProducesResponseType(typeof(VideoModelSettingDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResetVideoModel(CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await settings.ResetVideoModelAsync(actor, ct));
    }
}
