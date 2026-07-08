using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Campaigns;
using Acme.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/campaigns")]
[Authorize]
public sealed class CampaignsController(CampaignService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<CampaignDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Ok(await service.ListAsync(userId, ct));
    }

    [HttpPost]
    [ProducesResponseType(typeof(CampaignDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(
        [FromBody] CreateCampaignRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var result = await service.CreateAndSendAsync(userId, request, ct);
        return result.Kind switch
        {
            ResultKind.Success => StatusCode(StatusCodes.Status201Created, result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            ResultKind.Invalid => BadRequest(new { error = result.Error }),
            ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [HttpPost("audience-preview")]
    [ProducesResponseType(typeof(CampaignAudiencePreview), StatusCodes.Status200OK)]
    public async Task<IActionResult> AudiencePreview(
        [FromBody] CampaignAudienceRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.PreviewAudienceAsync(userId, request, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [HttpPost("{id}/retry")]
    [ProducesResponseType(typeof(CampaignDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Retry(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.RetryAsync(id, userId, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            ResultKind.Invalid => BadRequest(new { error = result.Error }),
            ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [HttpPost("{id}/stop")]
    [ProducesResponseType(typeof(CampaignDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Stop(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var result = await service.StopAsync(id, userId, ct);
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
