using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Ai;
using Acme.Application.Common;
using Acme.Application.Export;
using Acme.Application.Posters;
using Acme.Domain.Posters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/posters")]
[Authorize]
public sealed class PostersController(
    PosterService service,
    PosterAiService posterAi,
    PosterPhotoService photos) : ControllerBase
{
    /// <summary>
    /// Request ceiling for a canvas save. Above the service's own limit
    /// (CanvasLimitsOptions), so a pharmacist normally gets its plain message;
    /// this only stops a body large enough to be abuse from being read at all.
    /// </summary>
    private const long CanvasBodyLimit = 8 * 1024 * 1024;

    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<PosterSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] PosterStatus? status,
        [FromQuery] string? search,
        [FromQuery] string? sort,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        // Last-updated date: the poster you just edited is the one you want first.
        var query = new PosterQuery(userId, status, search, skip, Math.Min(take, 100),
            Window: ListWindow.From(sort, from, to));
        var items = await service.ListAsync(query, ct);
        var total = await service.CountAsync(query, ct);
        return Ok(new PagedResponse<PosterSummaryDto>(items, total, skip, take));
    }

    /// <summary>Designs a poster from a written brief and saves it.</summary>
    /// <remarks>
    /// <para>
    /// The pharmacy-facing counterpart to <c>POST /api/admin/templates/generate</c>.
    /// Same generator; different transaction. An administrator spends our money
    /// once, on our library. A pharmacy spends it repeatedly, on their own
    /// posters, against the allowance printed on the pricing page — so this path
    /// counts, rate-limits, and fills in their pharmacy details.
    /// </para>
    /// <para>
    /// <b>202 rather than 201.</b> The poster exists by the time this returns,
    /// so 201 would be defensible — but the design is a first draft the
    /// pharmacist is expected to open and edit, and 201 Created with a Location
    /// header suggests a finished resource. 200 with the poster is the honest
    /// shape: here is what we made, go and look at it.
    /// </para>
    /// <para>
    /// The caller's token is forwarded so ai-service can verify who is asking,
    /// exactly as the admin path does.
    /// </para>
    /// </remarks>
    [Audit("poster.generate", "poster")]
    [HttpPost("generate")]
    [ProducesResponseType(typeof(GeneratedPosterDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Generate(
        [FromBody] GeneratePosterRequest request,
        [FromServices] Application.Audit.IAuditScope audit,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        if (BearerToken() is not { } token) return Unauthorized();

        var result = await posterAi.GenerateAsync(userId, request, token, ct);
        if (result.IsSuccess)
        {
            // The poster is nested in the response, so the audit row is told
            // which one directly; and what kind of photo was asked for.
            audit.SetEntity("poster", result.Value!.Poster.Id);
            audit.AddMeta("photo", (request.Photo?.Mode ?? Application.Templates.PosterPhotoMode.Stock)
                .ToString().ToLowerInvariant());
        }
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            ResultKind.Forbidden => StatusCode(
                StatusCodes.Status403Forbidden, new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [ActivityLog("poster.view", "poster")]
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

    /// <summary>
    /// The rendered poster behind the image in a campaign email.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Anonymous, because the reader is a patient.</b> They have no account
    /// and no cookie; the signed token is the entire authorisation, and it names
    /// the exact blob it permits. There is no poster id in the route, so there
    /// is nothing to enumerate and no unsigned input to distrust.
    /// </para>
    /// <para>
    /// This exists so the poster is not inlined into every recipient's copy of
    /// the email. Mailjet duplicates an inline attachment per personalisation:
    /// measured at 244 KB per recipient, which is 244 MB for a campaign of a
    /// thousand and eighty seconds of uploading for a campaign of four. See
    /// <see cref="PosterImageToken"/>.
    /// </para>
    /// <para>
    /// Transcoded here rather than stored transcoded. The rendered PNG is shared
    /// with the kiosk and with social publishing and is cached by content hash;
    /// re-encoding it for email at its own boundary is what keeps that cache
    /// from having to hold a variant per destination. Re-encoding costs
    /// milliseconds, and the response is cacheable for a year — the blob key is
    /// a content hash, so the bytes behind a given URL never change.
    /// </para>
    /// <para>
    /// Every refusal is a 404 with the same body. A recipient can do nothing
    /// useful with the distinction between "expired", "wrong signature" and "no
    /// such blob", and telling them apart tells anyone probing the endpoint
    /// which of their guesses was closest.
    /// </para>
    /// </remarks>
    [HttpGet("email-image")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> EmailImage(
        [FromQuery] string? token,
        [FromServices] IPosterImageStore store,
        [FromServices] IImageTranscoder transcoder,
        [FromServices] ILogger<PostersController> log,
        CancellationToken ct = default)
    {
        if (!PosterImageToken.TryReadBlobKey(token, out var blobKey, out var reason))
        {
            log.LogInformation("Refused a poster email-image link: {Reason}", reason);
            return NotFound(new { error = "That image is no longer available." });
        }

        byte[] png;
        try
        {
            await using var stream = await store.OpenReadAsync(blobKey, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            png = buffer.ToArray();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "A signed poster image {BlobKey} could not be read back", blobKey);
            return NotFound(new { error = "That image is no longer available." });
        }

        // Null means "use the original", never "drop the image".
        var jpeg = transcoder.ToEmailJpeg(png);

        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return File(jpeg ?? png, jpeg is null ? "image/png" : "image/jpeg");
    }

    [Audit("poster.create", "poster")]
    [HttpPost]
    [RequestSizeLimit(CanvasBodyLimit)] // R2-12: a clear refusal comes from the service below this
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
            ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [Audit("poster.update", "poster")]
    [HttpPut("{id}")]
    [RequestSizeLimit(CanvasBodyLimit)] // R2-12: a clear refusal comes from the service below this
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

    [Audit("poster.duplicate", "poster")]
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
            ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [Audit("poster.status", "poster")]
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

    [Audit("poster.delete", "poster")]
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

    /* ---------------- Photos for "Design with AI" ---------------- */

    /// <summary>
    /// Uploads a photo for an AI design. Multipart form-data, field "file".
    /// </summary>
    /// <remarks>
    /// Stored at once, so the pharmacist sees a thumbnail — or a refusal — on
    /// pick, not a minute into a failed design. JPEG, PNG or WebP, judged by
    /// the bytes; 12MB, as for the admin upload (the browser downscales first).
    /// </remarks>
    [Audit("poster.photo_upload", "poster_photo")]
    [HttpPost("photos")]
    [RequestSizeLimit(PosterPhotoService.MaxUploadBytes + 256 * 1024)]
    [ProducesResponseType(typeof(StoredPosterPhoto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadPhoto(IFormFile? file, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        if (file is null) return BadRequest(new { error = "Choose a photo to upload." });

        await using var stream = file.OpenReadStream();
        var result = await photos.UploadAsync(userId, stream, file.Length, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// One of the caller's own photo uploads, for the picker's preview (SEC-15).
    /// </summary>
    /// <remarks>
    /// Looked up in the caller's own private area, so a name belonging to
    /// another pharmacy is simply not found. Private caching only.
    /// </remarks>
    [HttpGet("photos/{fileName}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Photo(string fileName, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        if (!PharmacyPhotos.IsValidFileName(fileName)) return NotFound();

        var stream = await photos.OpenUploadAsync(userId, fileName, ct);
        if (stream is null) return NotFound();

        Response.Headers.CacheControl = "private, max-age=3600";
        return File(stream, PharmacyPhotos.ContentTypeFor(fileName));
    }

    /// <summary>Searches stock photography for the picker. Rate-limited per person.</summary>
    [HttpGet("stock-photos")]
    [EnableRateLimiting(RateLimits.StockSearch)]
    [ProducesResponseType(typeof(IReadOnlyList<Application.Templates.StockPhoto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> StockPhotos(
        [FromQuery] string? query, [FromQuery] string? aspect, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        if (BearerToken() is not { } token) return Unauthorized();

        var result = await photos.SearchStockAsync(userId, query, aspect, token, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>Which photo sources are set up — so AI is hidden, with a reason, when it is not.</summary>
    [HttpGet("photo-options")]
    [ProducesResponseType(typeof(Application.Templates.ImageOptions), StatusCodes.Status200OK)]
    public async Task<IActionResult> PhotoOptions(CancellationToken ct)
    {
        if (BearerToken() is not { } token) return Unauthorized();
        var result = await photos.GetOptionsAsync(token, ct);
        return Ok(result.Value);
    }

    private string? CurrentUserId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>
    /// The caller's raw access token, for forwarding to ai-service.
    /// </summary>
    /// <remarks>
    /// Read from the header rather than re-minted, so the downstream call stays
    /// attached to this pharmacist. Same code and same reasoning as
    /// <c>VideosController.BearerToken</c>.
    /// </remarks>
    private string? BearerToken()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header)) return null;

        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }

}

public sealed record SetPosterStatusRequest(PosterStatus Status);
