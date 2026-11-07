using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Common;
using Acme.Application.Videos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/videos")]
[Authorize]
public sealed class VideosController(
    VideoService service,
    INarrationCatalog narration) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<VideoDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? sort,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct = default)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        // Created date: newest first unless sort=oldest; from inclusive, to exclusive.
        return Ok(await service.ListAsync(userId, ListWindow.From(sort, from, to), ct));
    }

    /// <summary>The voices this deployment can narrate with.</summary>
    /// <remarks>
    /// <para>
    /// Relayed from ai-service, which owns the answer because
    /// <c>ELEVENLABS_API_KEY</c> lives there and that key is what decides the
    /// provider. The dialog used to decide from the voice id and was visibly
    /// wrong on every hosted deployment: it offered Edge voices that the render
    /// would silently swap for ElevenLabs, and estimated those renders at a
    /// tenth of their real cost.
    /// </para>
    /// <para>
    /// <b>204 when ai-service cannot be reached.</b> Not a guessed default — a
    /// guess is how a pharmacist ends up picking a voice they will not get. The
    /// dialog offers no voice choice and lets the server pick.
    /// </para>
    /// </remarks>
    [HttpGet("narration")]
    [ProducesResponseType(typeof(NarrationOptionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Narration(CancellationToken ct)
    {
        if (CurrentUserId() is null) return Unauthorized();
        if (BearerToken() is not { } token) return Unauthorized();

        var options = await narration.GetAsync(token, ct);
        return options is null ? NoContent() : Ok(options);
    }

    [ActivityLog("video.view", "video")]
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(VideoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.GetAsync(id, userId, ct);
        return ToResponse(result, () => Ok(result.Value));
    }

    [Audit("video.create", "video")]
    [HttpPost]
    [ProducesResponseType(typeof(VideoDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CreateVideoRequest request, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.CreateAsync(userId, request, ct);
        return ToResponse(result, () => StatusCode(StatusCodes.Status202Accepted, result.Value));
    }

    [Audit("video.retry", "video")]
    [HttpPost("{id}/retry")]
    [ProducesResponseType(typeof(VideoDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Retry(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.RetryAsync(id, userId, ct);
        return ToResponse(result, () => StatusCode(StatusCodes.Status202Accepted, result.Value));
    }

    /// <summary>Says what a requested change would do. Runs nothing.</summary>
    /// <remarks>
    /// Separate from the confirm below on purpose. A change request is routed by
    /// interpreting the pharmacist's own sentence, and getting that wrong is
    /// either expensive or invisible — so nothing happens until they have seen
    /// what it will do and agreed to it.
    /// </remarks>
    [NoAudit("Read-only plan of a change; the confirmed refine is audited.")]
    [HttpPost("{id}/refine/preview")]
    [ProducesResponseType(typeof(RefinePlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PreviewRefine(
        string id, [FromBody] RefineRequest request, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        // Forwarded to ai-service, which verifies the caller itself rather than
        // trusting that we did.
        if (BearerToken() is not { } token) return Unauthorized();
        var result = await service.PreviewRefineAsync(
            id, userId, request.Instruction, token, ct);
        return ToResponse(result, () => Ok(result.Value));
    }

    /// <summary>Runs the change that was previewed and agreed to.</summary>
    /// <remarks>
    /// The instruction is sent again and has to match the plan on record. That
    /// is what stops a request being assembled that carries one sentence past a
    /// plan made for another — the difference between those two sentences being
    /// the difference between a free re-cut and a billable rewrite.
    /// </remarks>
    [Audit("video.refine", "video")]
    [HttpPost("{id}/refine")]
    [ProducesResponseType(typeof(VideoDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Refine(
        string id, [FromBody] RefineRequest request, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.ConfirmRefineAsync(id, userId, request.Instruction, ct);
        return ToResponse(result, () => StatusCode(StatusCodes.Status202Accepted, result.Value));
    }

    /// <summary>Adds the pharmacy's own pictures to a finished video.</summary>
    /// <remarks>
    /// <para>
    /// Takes blob keys, not files — the pictures are uploaded one at a time to
    /// <c>POST /api/videos/images</c> first, so a wrong or oversized one is
    /// refused while the pharmacist is still looking at the file they picked.
    /// </para>
    /// <para>
    /// <b>No preview, and no instruction echoed back.</b> Unlike
    /// <see cref="Refine"/>, there is nothing here that a model interpreted and
    /// therefore nothing that could have been interpreted wrongly. The guard
    /// that matters on this route is ownership of the keys, which the service
    /// applies exactly as the create path does.
    /// </para>
    /// </remarks>
    [Audit("video.pictures.add", "video")]
    [HttpPost("{id}/pictures")]
    [ProducesResponseType(typeof(VideoDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddPictures(
        string id, [FromBody] AddVideoPicturesRequest request, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.AddPicturesAsync(id, userId, request.Images, ct);
        return ToResponse(result, () => StatusCode(StatusCodes.Status202Accepted, result.Value));
    }

    /// <summary>Every shot of a finished video, for the refine dialog's picker.</summary>
    [HttpGet("{id}/shots")]
    [ProducesResponseType(typeof(IReadOnlyList<VideoShotDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Shots(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.ListShotsAsync(id, userId, ct);
        return ToResponse(result, () => Ok(result.Value));
    }

    /// <summary>A still of one shot, as rendered — a tile in the picker.</summary>
    [HttpGet("{id}/shots/{index:int}/thumbnail")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ShotThumbnail(string id, int index, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return StreamResult(await service.OpenShotThumbnailAsync(id, userId, index, ct));
    }

    /// <summary>Re-cuts a finished video with new footage at the shots the pharmacist tapped.</summary>
    /// <remarks>
    /// No preview and no instruction echoed back, like <see cref="AddPictures"/>:
    /// the shot is the tile they tapped, so nothing was interpreted that they
    /// would need to check. See <c>VideoService.ChangeShotsAsync</c>.
    /// </remarks>
    [Audit("video.shots.update", "video")]
    [HttpPost("{id}/shots")]
    [ProducesResponseType(typeof(VideoDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ChangeShots(
        string id, [FromBody] ChangeShotsRequest request, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.ChangeShotsAsync(id, userId, request.Shots, ct);
        return ToResponse(result, () => StatusCode(StatusCodes.Status202Accepted, result.Value));
    }

    [Audit("video.delete", "video")]
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.DeleteAsync(id, userId, ct);
        return ToResponse(result, () => NoContent());
    }

    /// <summary>Owner-authenticated streaming of the rendered MP4 (or JPG
    /// thumbnail). This replaces the previous SAS-URL approach so browsers
    /// never hit Azurite directly — that was surfacing BlobNotFound errors
    /// intermittently in dev.</summary>
    /// <summary>Attach one of the pharmacy's own pictures, before creating a video.</summary>
    /// <remarks>
    /// <para>
    /// Multipart form-data, field name "file". Returns the blob key to hand
    /// back in <c>CreateVideoRequest.Images</c>.
    /// </para>
    /// <para>
    /// <b>Separate from create, deliberately.</b> The dialog uploads as soon as
    /// a file is chosen so a thumbnail appears and a wrong picture can be
    /// removed before a render is spent — the same reasoning the template
    /// composer uses for resolving a photograph before designing a layout
    /// around it. Posting four 8MB files as part of create would also make the
    /// create request a multi-megabyte upload that fails whole.
    /// </para>
    /// </remarks>
    [Audit("video.image.upload", "video_image")]
    [HttpPost("images")]
    // <b>The ceiling, not the limit.</b>
    //
    // Set to the cap itself, this attribute rejected an oversized upload in the
    // framework — before the action, before `WhyFileRefused`, before any of our
    // wording. What came back was ASP.NET's own validation envelope:
    // "Failed to read the request form. Request body too large." wrapped in
    // ProblemDetails, with no field the BFF could read, so the pharmacist was
    // told only "That image could not be attached." A 20MB photograph is the
    // commonest way to meet this endpoint and it was the worst-explained.
    //
    // At twice the cap, a file in the range people actually pick reaches the
    // action and gets the real sentence with its real size in it. The attribute
    // keeps its other job — refusing a body so large that reading it is itself
    // the attack — and `VideoImagePolicy.MaxBytes` remains the only number that
    // decides what is allowed.
    [RequestSizeLimit(VideoImagePolicy.MaxBytes * 2)]
    [ProducesResponseType(typeof(VideoImageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadImage(IFormFile file, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Choose an image to attach." });

        await using var stream = file.OpenReadStream();
        var result = await service.UploadImageAsync(
            userId, stream, file.ContentType, file.Length, file.FileName, ct);

        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    [HttpGet("{id}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Download(string id, CancellationToken ct) =>
        StreamOwned(id, thumbnail: false, ct);

    [HttpGet("{id}/thumbnail")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Thumbnail(string id, CancellationToken ct) =>
        StreamOwned(id, thumbnail: true, ct);

    /// <summary>Public token-guarded streaming for outgoing email links.
    /// Recipients don't have auth cookies — the token proves the sender
    /// blessed this specific video for this specific window.</summary>
    [HttpGet("{id}/public-download")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PublicDownload(
        string id,
        [FromQuery] string token,
        [FromQuery] bool thumb = false,
        CancellationToken ct = default)
    {
        var result = await service.OpenPublicDownloadAsync(id, token, thumb, ct);
        return StreamResult(result);
    }

    private async Task<IActionResult> StreamOwned(string id, bool thumbnail, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.OpenDownloadAsync(id, userId, thumbnail, ct);
        return StreamResult(result);
    }

    private IActionResult StreamResult(Result<VideoBlobStream> result)
    {
        return result.Kind switch
        {
            ResultKind.Success => result.Value!.ETag is { } tag
                // With a validator, ASP.NET also answers If-None-Match (304)
                // and If-Range correctly — both of which a browser replaying a
                // cached video relies on.
                ? File(result.Value!.Content, result.Value!.ContentType,
                    lastModified: null,
                    entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(tag),
                    enableRangeProcessing: true)
                : File(result.Value!.Content, result.Value!.ContentType, enableRangeProcessing: true),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    private IActionResult ToResponse<T>(Result<T> result, Func<IActionResult> onSuccess) =>
        result.Kind switch
        {
            ResultKind.Success => onSuccess(),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            ResultKind.Invalid => BadRequest(new { error = result.Error }),
            ResultKind.Conflict => Conflict(new { error = result.Error }),
            ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
            ResultKind.Unauthorized => Unauthorized(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };

    private string? CurrentUserId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>
    /// The caller's raw access token, for forwarding to ai-service.
    /// </summary>
    /// <remarks>
    /// Read from the header rather than re-minted, so the downstream call stays
    /// attached to this pharmacist — the same reasoning, and the same code, as
    /// <c>AdminController.BearerToken</c>. Duplicated rather than shared
    /// because the alternative is a base controller that exists to hold one
    /// eight-line method; if a third caller appears, that changes.
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
