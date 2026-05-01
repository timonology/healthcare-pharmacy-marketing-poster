using Acme.Application.Common;
using Acme.Application.Templates;
using Acme.Domain.Templates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/templates")]
[Authorize]
public sealed class TemplatesController(TemplateService service) : ControllerBase
{
    /// <summary>List published templates with optional category / search filtering.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<TemplateSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] TemplateCategory? category,
        [FromQuery] string? search,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var query = new TemplateQuery(category, search, IsPublished: true, skip, Math.Min(take, 100));
        var items = await service.ListAsync(query, ct);
        var total = await service.CountAsync(query, ct);
        return Ok(new PagedResponse<TemplateSummaryDto>(items, total, skip, take));
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(TemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var result = await service.GetAsync(id, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>
    /// Create a template. In production this should be admin-only — gate with
    /// a role/policy check before exposing publicly.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(TemplateDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] UpsertTemplateRequest request,
        CancellationToken ct)
    {
        var result = await service.CreateAsync(request, ct);
        return result.Kind switch
        {
            ResultKind.Success => CreatedAtAction(nameof(Get), new { id = result.Value!.Id }, result.Value),
            ResultKind.Invalid => BadRequest(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(TemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] UpsertTemplateRequest request,
        CancellationToken ct)
    {
        var result = await service.UpdateAsync(id, request, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            ResultKind.Invalid => BadRequest(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var result = await service.DeleteAsync(id, ct);
        return result.Kind switch
        {
            ResultKind.Success => NoContent(),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }
}

public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Skip,
    int Take);
