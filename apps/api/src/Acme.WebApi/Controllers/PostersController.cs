using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Common;
using Acme.Application.Posters;
using Acme.Domain.Posters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/posters")]
[Authorize]
public sealed class PostersController(PosterService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<PosterSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] PosterStatus? status,
        [FromQuery] string? search,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var query = new PosterQuery(userId, status, search, skip, Math.Min(take, 100));
        var items = await service.ListAsync(query, ct);
        var total = await service.CountAsync(query, ct);
        return Ok(new PagedResponse<PosterSummaryDto>(items, total, skip, take));
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PosterDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var result = await service.GetAsync(id, userId, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [HttpPost]
    [ProducesResponseType(typeof(PosterDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CreatePosterRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var result = await service.CreateAsync(userId, request, ct);
        return result.Kind switch
        {
            ResultKind.Success => CreatedAtAction(
                nameof(Get),
                new { id = result.Value!.Id },
                result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            ResultKind.Invalid => BadRequest(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(PosterDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] UpdatePosterRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var result = await service.UpdateAsync(id, userId, request, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            ResultKind.Invalid => BadRequest(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [HttpPost("{id}/duplicate")]
    [ProducesResponseType(typeof(PosterDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Duplicate(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var result = await service.DuplicateAsync(id, userId, ct);
        return result.Kind switch
        {
            ResultKind.Success => CreatedAtAction(
                nameof(Get),
                new { id = result.Value!.Id },
                result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [HttpPatch("{id}/status")]
    [ProducesResponseType(typeof(PosterDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetStatus(
        string id,
        [FromBody] SetPosterStatusRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var result = await service.SetStatusAsync(id, userId, request.Status, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var result = await service.DeleteAsync(id, userId, ct);
        return result.Kind switch
        {
            ResultKind.Success => NoContent(),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    private string? CurrentUserId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
}

public sealed record SetPosterStatusRequest(PosterStatus Status);
