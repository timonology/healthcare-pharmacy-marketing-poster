using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.BrandKit;
using Acme.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/brand-kit")]
[Authorize]
public sealed class BrandKitController(BrandKitService service) : ControllerBase
{
    private const long MaxLogoBytes = 5 * 1024 * 1024; // 5 MB

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/svg+xml",
        "image/webp",
    };

    /// <summary>Get the current user's brand kit.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(BrandKitDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var result = await service.GetForOwnerAsync(userId, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>Create or update the current user's brand kit.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(BrandKitDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upsert(
        [FromBody] UpsertBrandKitRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var result = await service.UpsertAsync(userId, request, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.Invalid => BadRequest(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>Upload a new logo image. Multipart form-data, field name "file".</summary>
    [HttpPost("logo")]
    [RequestSizeLimit(MaxLogoBytes)]
    [ProducesResponseType(typeof(BrandKitDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UploadLogo(IFormFile file, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        if (file is null || file.Length == 0)
            return BadRequest(new { error = "File is required." });

        if (file.Length > MaxLogoBytes)
            return BadRequest(new { error = $"File exceeds {MaxLogoBytes / (1024 * 1024)}MB limit." });

        if (!AllowedContentTypes.Contains(file.ContentType))
            return BadRequest(new { error = $"Unsupported content type: {file.ContentType}" });

        var ext = Path.GetExtension(file.FileName);
        await using var stream = file.OpenReadStream();

        var result = await service.UploadLogoAsync(userId, stream, file.ContentType, ext, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    private string? CurrentUserId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
}
