using Acme.Domain.Videos;

namespace Acme.Application.Videos;

/// <summary>A change a pharmacist has asked for, in their own words.</summary>
/// <remarks>
/// The same shape for preview and confirm, because the confirm has to carry the
/// instruction back to be checked against the plan on record. Two shapes would
/// invite one of them to drift.
/// </remarks>
public sealed record RefineRequest(string Instruction);

/// <summary>Pictures to add to a finished video, by blob key.</summary>
/// <remarks>
/// Its own request type rather than an optional field on
/// <see cref="RefineRequest"/>: that one carries an instruction which must match
/// a stored plan, and these two operations agree on nothing except the video
/// they act on. Folding them together would have meant a required instruction
/// that is meaningless here, or an optional one that quietly changes which of
/// two code paths runs.
/// </remarks>
public sealed record AddVideoPicturesRequest(IReadOnlyList<VideoImageDto> Images);

/// <summary>What a requested change would do, before it is agreed to.</summary>
/// <param name="CanRun">
/// Whether pressing the button will actually do anything. False covers three
/// different situations — an instruction we could not route, a stage that is
/// not open yet, and a video that cannot be changed right now — and
/// <paramref name="Reason"/> is what distinguishes them to the pharmacist.
/// </param>
/// <param name="Stage">
/// Named for support and for the browser test suite, not for the pharmacy user.
/// Null when the instruction could not be routed.
/// </param>
/// <param name="Free">
/// True when running this costs nothing. The only honest answer today: every
/// stage that spends is closed, so anything runnable is free.
/// </param>
public sealed record RefinePlanDto(
    bool CanRun,
    string? Stage,
    string Summary,
    string Reason,
    bool Free);

public sealed record VideoDto(
    string Id,
    string OwnerId,
    string Name,
    string Topic,
    VideoStatus Status,
    VideoScriptSource ScriptSource,
    VideoVisualStyle VisualStyle,
    string Aspect,
    int DurationSeconds,
    string? DownloadUrl,
    string? ThumbnailUrl,
    string? Note,

    /// <summary>
    /// The technical reason the last render failed. Never shown in the app.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carried on the DTO so <c>pnpm smoke</c> — which signs in as an ordinary
    /// pharmacy account — can print why a production render failed without
    /// anyone opening a log stream. That round trip is what this field exists to
    /// remove.
    /// </para>
    /// <para>
    /// It is an exception type and message, never a stack trace or a file path;
    /// the worker sanitises it before storing. The web app renders
    /// <see cref="Note"/> and only <see cref="Note"/> — a pharmacist should not
    /// see this, and a bug that put it on screen would be a UI bug, not a leak
    /// of anything beyond a provider's error text.
    /// </para>
    /// </remarks>
    string? Diagnostic,

    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,

    /// <summary>Whether pressing Retry would be accepted right now.</summary>
    /// <remarks>
    /// <para>
    /// Answered here rather than inferred from <see cref="Status"/>, because the
    /// client cannot tell the two <c>Rendering</c> cases apart. A render still
    /// running and a render whose worker died look identical from outside: the
    /// difference is whether the lease has lapsed, and that is a server fact
    /// involving a lease the DTO deliberately does not carry and a clock the
    /// browser should not be comparing against.
    /// </para>
    /// <para>
    /// The UI gated Retry on <c>Failed</c>, which is exactly the status a
    /// stranded render never reaches — so the recovery path existed in the API
    /// and was unreachable by the person who needed it.
    /// </para>
    /// </remarks>
    bool CanRetry,

    /// <summary>How many of the pharmacy's own pictures this video carries.</summary>
    /// <remarks>
    /// A count, not the pictures. What the dialog needs to know is how much room
    /// is left before <c>MAX_VIDEO_IMAGES</c>, and answering that with the blob
    /// keys would put storage paths on the wire — read by nothing, logged by
    /// whatever logs responses, and a map of the account's storage for anyone
    /// who gets hold of one. The keys the client does send are ones it uploaded
    /// itself moments earlier.
    /// </remarks>
    int ImageCount = 0);

/// <summary>
/// Single entry-point for creating a video. The pipeline branches on
/// <see cref="ScriptSource"/> and <see cref="VisualStyle"/>. Callers can
/// omit optional fields for sensible defaults (Ai + Realistic).
/// </summary>
public sealed record CreateVideoRequest(
    string Topic,
    string? Name = null,
    VideoScriptSource ScriptSource = VideoScriptSource.Ai,
    VideoVisualStyle VisualStyle = VideoVisualStyle.Realistic,
    string? Description = null,
    string? CustomScript = null,
    string? VoiceId = null,
    string? StyleTone = null,

    /// <summary>
    /// Up to four of the pharmacy's own pictures, already uploaded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Optional, and last in the list so every existing caller compiles
    /// unchanged — a video with no pictures of its own is the ordinary case.
    /// </para>
    /// <para>
    /// Already-stored keys rather than bytes: the dialog uploads first so the
    /// pharmacist can see a thumbnail and remove one <i>before</i> spending a
    /// render, the same reasoning as resolving a template's photograph before
    /// designing a layout around it.
    /// </para>
    /// </remarks>
    IReadOnlyList<VideoImageDto>? Images = null);

/// <summary>One picture the pharmacy attached, as it crosses the wire.</summary>
/// <param name="Source">"upload", "logo" or "poster".</param>
public sealed record VideoImageDto(
    string BlobKey,
    string Source = "upload",
    string? Description = null,

    /// <summary>
    /// Which part of the picture to keep when it is cropped to 9:16, 0–1.
    /// Centre when the client says nothing, which is what it always did.
    /// </summary>
    double FocalX = VideoImage.Centre,
    double FocalY = VideoImage.Centre,

    /// <summary>
    /// How much of the picture fits the frame. Omitted <i>or null</i> means 1.0
    /// — the crop every picture had before zoom existed.
    /// </summary>
    /// <remarks>
    /// Nullable so an explicit <c>"zoom": null</c> means "absent" rather than a
    /// 400 for the whole video: a plain <c>double</c> refuses null at binding,
    /// before anything could fall back.
    /// </remarks>
    double? Zoom = null);
