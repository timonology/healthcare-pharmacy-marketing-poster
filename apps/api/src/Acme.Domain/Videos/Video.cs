using Acme.Domain.Common;

namespace Acme.Domain.Videos;

public enum VideoSource
{
    // Ai-generated pipeline (research → script → voice → visuals → editor).
    AiPipeline = 0,
}

public enum VideoStatus
{
    Pending   = 0,
    Rendering = 1,
    Ready     = 2,
    Failed    = 3,
}

public enum VideoScriptSource
{
    /// <summary>Agent researches + writes from topic (default).</summary>
    Ai = 0,
    /// <summary>Agent writes, using Description as required creative direction.</summary>
    Guided = 1,
    /// <summary>Bypass research + writing; CustomScript is the narration.</summary>
    Custom = 2,
}

public enum VideoVisualStyle
{
    /// <summary>Pexels stock footage.</summary>
    Realistic = 0,
    /// <summary>Replicate Flux Schnell one image per beat (Pro only).</summary>
    AiImagery = 1,
}

/// <summary>
/// A marketing video the user has asked the agent to generate. The pipeline
/// runs asynchronously in the ai-service Python worker; this entity is what
/// the .NET side owns (tier gating, ownership, name/topic, blob keys).
/// </summary>
public sealed class Video : Entity
{
    public string OwnerId    { get; private set; } = default!;
    public string Name       { get; private set; } = default!;

    /// <summary>What the pipeline will make a video ABOUT. Free-text.</summary>
    public string Topic      { get; private set; } = default!;

    /// <summary>Freeform tone hint fed to the scriptwriter.</summary>
    public string? StyleTone { get; private set; }

    public VideoSource Source { get; private set; } = VideoSource.AiPipeline;
    public VideoStatus Status { get; private set; } = VideoStatus.Pending;

    public VideoScriptSource ScriptSource { get; private set; } = VideoScriptSource.Ai;
    public VideoVisualStyle  VisualStyle  { get; private set; } = VideoVisualStyle.Realistic;

    /// <summary>Creative direction (Guided mode) or the whole narration (Custom mode).</summary>
    public string? Description { get; private set; }
    public string? CustomScript { get; private set; }

    /// <summary>Edge-TTS voice id, or "11labs:{voice_uuid}" for ElevenLabs.</summary>
    public string? VoiceId { get; private set; }

    /// <summary>
    /// The pharmacy's own pictures, pinned into the visuals stage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty for almost every video, and that is the ordinary case — attaching
    /// photographs is optional and the pipeline is built around stock footage.
    /// </para>
    /// <para>
    /// <b>Only ever added to, and only by <see cref="AddPictures"/>.</b> A
    /// retry, and a refine that answers a sentence, both keep exactly the
    /// pictures that were here — the pharmacist asked for a change to something
    /// else, and quietly re-cutting with a different set would not be that
    /// change. Attaching a photograph is its own explicit request.
    /// </para>
    /// <para>
    /// Never removed. Dropping one would strand its blob outside
    /// <c>DeleteAsync</c>'s sweep, and "take that picture out again" is a
    /// different feature from "add one" — worth building on its own terms if it
    /// is wanted, rather than falling out of this setter.
    /// </para>
    /// </remarks>
    public IReadOnlyList<VideoImage> Images { get; private set; } = Array.Empty<VideoImage>();

    public string Aspect         { get; private set; } = "9:16";
    public int    DurationSeconds { get; private set; } = 15;

    /// <summary>Blob key of the produced MP4 (set by the Python worker when Ready).</summary>
    public string? BlobKey          { get; private set; }
    public string? ThumbnailBlobKey { get; private set; }
    public string? Note             { get; private set; }

    /// <summary>
    /// Why the last render failed, in technical terms — for whoever has to fix
    /// it, never for the pharmacist.
    /// </summary>
    /// <remarks>
    /// <see cref="Note"/> is written for the person who runs the pharmacy, which
    /// is right, and which left the real reason living only in a log stream: a
    /// production failure could be reported but not diagnosed without access to
    /// the host. Written by the Python worker alone — the exception type and
    /// message, no stack frames and no file paths.
    /// </remarks>
    public string? Diagnostic       { get; private set; }

    /// <summary>Which worker is rendering this, and until when.</summary>
    /// <remarks>
    /// <para>
    /// <b>Without these, a render that dies is stranded for good.</b> The worker
    /// claimed a row by flipping <c>Pending</c> to <c>Rendering</c> and nothing
    /// ever read that status back: no lease, no expiry, no sweeper on either
    /// side. Its claim filter matches <c>Pending</c> only, so it would never
    /// pick the row up again, and <see cref="Retry"/> refused anything that was
    /// not <c>Failed</c> — which a stranded job never becomes.
    /// </para>
    /// <para>
    /// The cost lands on the pharmacy, because the quota is spent when the video
    /// is created, not when it succeeds. A container restart mid-render, or
    /// ffmpeg taking an OOM kill, took a credit and left a row that could
    /// neither finish nor be retried.
    /// </para>
    /// <para>
    /// A lease rather than a lock, for the reason the campaign queue uses one: a
    /// worker that is killed cannot release anything, so expiry is the only way
    /// the job comes back.
    /// </para>
    /// </remarks>
    public string?   LeaseOwner        { get; private set; }
    public DateTime? LeaseExpiresAtUtc { get; private set; }

    /// <summary>
    /// Rendering, but by nobody — the claim has lapsed and no worker is coming.
    /// </summary>
    /// <remarks>
    /// A <c>Rendering</c> row with no lease at all is included deliberately:
    /// that is every job claimed before leases existed, and they are exactly the
    /// ones that have been stuck since.
    /// </remarks>
    /// <summary>
    /// How long a <c>Rendering</c> row with no lease at all is given before it
    /// is presumed abandoned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A missing lease means one of two very different things, and nothing on
    /// the row distinguishes them: the render was claimed by a worker built
    /// before leases existed and is going along fine, or it was abandoned. The
    /// old rule called both stranded <b>immediately</b>, so a perfectly healthy
    /// render showed "Stalled — try again" under a progress bar that was still
    /// moving. Which is how it was reported.
    /// </para>
    /// <para>
    /// <see cref="UpdatedAtUtc"/> settles it. The worker's progress callback
    /// writes the note, the lease and this timestamp in one update, so a row
    /// that is genuinely progressing is being touched every second or so. Quiet
    /// for longer than a lease would have lasted is the honest test of
    /// abandonment, and it still recovers the legacy rows the null case was
    /// added for.
    /// </para>
    /// <para>
    /// Deliberately longer than the worker's <c>LEASE_SECONDS</c> rather than
    /// equal to it. This is a fallback for rows that predate leases, not a
    /// second copy of the lease duration — the only dangerous direction is being
    /// *shorter* than the worker's lease, which would declare live renders
    /// stalled again. <c>RefineStageContractTests</c> pins that inequality.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan UnleasedRenderGrace = TimeSpan.FromMinutes(15);

    public bool IsStranded(DateTime nowUtc) =>
        Status == VideoStatus.Rendering
        && (LeaseExpiresAtUtc is { } expiry
            ? expiry < nowUtc
            // No lease: judge by silence, not by absence.
            : UpdatedAtUtc.Add(UnleasedRenderGrace) < nowUtc);

    /// <summary>
    /// The most pictures one video may carry.
    /// </summary>
    /// <remarks>
    /// Mirrored by <c>VideoImagePolicy.MaxImages</c> at the application edge and
    /// <c>MAX_VIDEO_IMAGES</c> in shared-types, for the same reason the refine
    /// instruction limit has three copies: the browser needs a literal to stop
    /// somebody at, and the domain needs the rule to be true regardless.
    /// </remarks>
    public const int VideoImageLimit = 4;

    private Video() { }

    public static Video Create(
        string ownerId,
        string name,
        string topic,
        VideoScriptSource scriptSource,
        VideoVisualStyle visualStyle,
        string? description,
        string? customScript,
        string? voiceId,
        string? styleTone,
        IReadOnlyList<VideoImage>? images = null)
    {
        Guard(ownerId, name);
        if (string.IsNullOrWhiteSpace(topic))
            throw new DomainException("Topic is required.");
        if (topic.Length > 400)
            throw new DomainException("Topic must be 400 characters or fewer.");

        if (scriptSource == VideoScriptSource.Custom && string.IsNullOrWhiteSpace(customScript))
            throw new DomainException("Custom script is required in Custom mode.");
        if (customScript is { Length: > 4000 })
            throw new DomainException("Custom script must be 4000 characters or fewer.");
        if (description is { Length: > 800 })
            throw new DomainException("Description must be 800 characters or fewer.");

        // The count is checked here as well as at the edge. The controller
        // refuses a bad request with a readable message, which is the right
        // place for the pharmacist's experience; this is the place that makes it
        // true for every caller, including a future one that is not a controller.
        if (images is { Count: > VideoImageLimit })
            throw new DomainException(
                $"A video can carry at most {VideoImageLimit} of your own images.");

        return new Video
        {
            Id            = Guid.NewGuid().ToString("N"),
            OwnerId       = ownerId,
            Name          = name.Trim(),
            Topic         = topic.Trim(),
            StyleTone     = string.IsNullOrWhiteSpace(styleTone) ? null : styleTone!.Trim(),
            Source        = VideoSource.AiPipeline,
            Status        = VideoStatus.Pending,
            ScriptSource  = scriptSource,
            VisualStyle   = visualStyle,
            Description   = description?.Trim(),
            CustomScript  = customScript?.Trim(),
            VoiceId       = string.IsNullOrWhiteSpace(voiceId) ? null : voiceId!.Trim(),
            Images        = images is null ? Array.Empty<VideoImage>() : images.ToArray(),
        };
    }

    /// <summary>
    /// The pharmacy's identity as it stood when this attempt was queued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stamped, not looked up.</b> The render worker is a separate Python
    /// process, so the alternative was for it to read the profile and the brand
    /// kit and apply the precedence rule itself — a second implementation of
    /// <see cref="BrandKit.PharmacyBrand"/> that would have to agree with the C#
    /// one by hand, forever. That is the fault shape behind most of what has
    /// gone wrong here, so the rule stays in one language and the worker reads
    /// a field.
    /// </para>
    /// <para>
    /// The cost is staleness, and it is bounded by refreshing on
    /// <see cref="Retry"/>: a pharmacy that corrects its name and retries gets
    /// the correction. A render already under way keeps the identity it started
    /// with, which is also the honest answer — it is what was ordered.
    /// </para>
    /// <para>
    /// Null on every video queued before this existed. Absent branding renders
    /// exactly as it always did, so old rows need no migration.
    /// </para>
    /// </remarks>
    public BrandKit.PharmacyBrand? Brand { get; private set; }

    /// <summary>Records the pharmacy identity this attempt should render with.</summary>
    public void StampBrand(BrandKit.PharmacyBrand? brand)
    {
        Brand = brand;
        Touch();
    }

    /// <summary>The refine this video is about to run, or has been offered.</summary>
    /// <remarks>
    /// Survives the render it describes, so the worker can read what was asked
    /// for and the pharmacist can see what they last changed. Cleared only by
    /// the next plan.
    /// </remarks>
    public RefinePlan? Refine { get; private set; }

    /// <summary>Records what a refine would do, without starting it.</summary>
    public void ProposeRefine(RefinePlan plan)
    {
        if (Status != VideoStatus.Ready)
            throw new DomainException(ChangeRefusedBecause(Status));

        Refine = plan;
        Touch();
    }

    /// <summary>
    /// Queues the agreed refine, putting the video back in the render queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Back to <see cref="VideoStatus.Pending"/> rather than a status of its
    /// own. The worker's claim already takes Pending rows atomically, renews a
    /// lease over them and recovers them when it dies — all of which a refine
    /// needs exactly as much as a first render does. A parallel status would
    /// mean a second claim path, and the two would have to agree by hand about
    /// leases forever after.
    /// </para>
    /// <para>
    /// The finished video's blob keys stay. The pharmacist keeps watching the
    /// video they already have while the new cut is made, and if the refine
    /// fails they still have it. Overwriting comes when the new render
    /// uploads.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Adds pictures the pharmacist just attached and queues a re-cut.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Appends rather than replaces. "Add an image to the video as well" is the
    /// request, and the pictures chosen at create were chosen deliberately —
    /// silently dropping them to make room would be a change nobody asked for.
    /// </para>
    /// <para>
    /// The cap counts what is already here plus what is arriving, which is the
    /// only reading that holds: checking the new ones alone would let four be
    /// added to four.
    /// </para>
    /// </remarks>
    public void AddPictures(IReadOnlyList<VideoImage> pictures, DateTime nowUtc)
    {
        if (pictures is null || pictures.Count == 0)
            throw new DomainException("Choose at least one picture to add.");

        if (Images.Count + pictures.Count > VideoImageLimit)
        {
            var room = VideoImageLimit - Images.Count;
            throw new DomainException(
                room <= 0
                    ? $"This video already has {VideoImageLimit} of your pictures, "
                      + "which is the most it can carry."
                    : $"This video already has {Images.Count}, so there is room for "
                      + $"{room} more — you chose {pictures.Count}.");
        }

        // Status is checked by StartRefine below, before anything here is kept.
        // Ordering matters: appending first would leave the pictures attached to
        // a video whose re-cut was then refused, and the next refine would pin
        // them without anybody asking for it.
        StartRefine(RefinePlan.ForPictures(pictures.Count, nowUtc));
        Images = Images.Concat(pictures).ToArray();
    }

    public void StartRefine(RefinePlan plan)
    {
        if (Status != VideoStatus.Ready)
            throw new DomainException(ChangeRefusedBecause(Status));

        Refine = plan.Confirm();
        Status = VideoStatus.Pending;
        Note = "Queued for changes…";
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
        Touch();
    }

    private static string ChangeRefusedBecause(VideoStatus status) => status switch
    {
        VideoStatus.Pending =>
            "This video is already waiting to be made. Give it a moment.",
        VideoStatus.Rendering =>
            "This video is being made right now. Changes can be asked for once "
            + "it has finished.",
        VideoStatus.Failed =>
            "This video did not finish, so there is nothing to change yet. "
            + "Retry it first.",
        _ => "This video cannot be changed at the moment.",
    };

    public void UpdateName(string name)
    {
        Guard(OwnerId, name);
        Name = name.Trim();
        Touch();
    }

    /// <summary>Taken by a worker until <paramref name="until"/>.</summary>
    public void Claim(string worker, DateTime until)
    {
        Status            = VideoStatus.Rendering;
        LeaseOwner        = worker;
        LeaseExpiresAtUtc = until;
        Touch();
    }

    /// <summary>Extends the claim while the pipeline is still making progress.</summary>
    /// <remarks>
    /// Called as each stage reports in. A render is minutes of research, speech
    /// synthesis, downloads and an ffmpeg encode, and any one of those can be
    /// slow without being stuck — so the lease has to be renewed from evidence
    /// of progress rather than set once and hoped over.
    /// </remarks>
    public void RenewLease(DateTime until)
    {
        LeaseExpiresAtUtc = until;
        Touch();
    }

    /// <summary>
    /// Queue another pipeline attempt, without creating a row and without
    /// re-charging the quota.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Valid from <c>Failed</c>, and from a <c>Rendering</c> job whose lease has
    /// lapsed. <b>The second case is the one that was missing</b>, and it is the
    /// likelier failure: a render killed mid-flight never reaches <c>Failed</c>,
    /// because reaching it requires the worker that died to write it. The
    /// pharmacy was charged at creation, so refusing the retry means they paid
    /// for a video that can never be produced.
    /// </para>
    /// <para>
    /// Still refused for <c>Ready</c> (delete and make a new one) and for a
    /// <c>Rendering</c> job with a live lease, which really is running.
    /// </para>
    /// <para>
    /// No quota interaction here, deliberately, and there is a test pinning it.
    /// <c>EnforceVideoLimit</c> runs in <c>CreateAsync</c> and spends the
    /// monthly allowance or a purchased credit; a retry creates nothing, so it
    /// spends nothing. Anything added to this method later that charges again
    /// would make a failed render cost twice.
    /// </para>
    /// </remarks>
    public void Retry(DateTime nowUtc)
    {
        if (Status != VideoStatus.Failed && !IsStranded(nowUtc))
        {
            throw new DomainException(Status switch
            {
                VideoStatus.Ready =>
                    "This video has already rendered. Delete it and create a new one.",
                VideoStatus.Rendering =>
                    "This video is still rendering. Give it a moment.",
                _ => "Only a failed video can be retried.",
            });
        }

        Status            = VideoStatus.Pending;
        Note              = null;
        // Cleared here for the in-memory object; the stored field is unset by
        // the worker when it claims the job, because this side does not write it.
        Diagnostic        = null;
        LeaseOwner        = null;
        LeaseExpiresAtUtc = null;

        // Blob keys stay so if the previous attempt did upload before
        // failing, the old bytes are still reachable while the retry
        // is in flight. They'll be overwritten by the new render.
        Touch();
    }

    private static void Guard(string ownerId, string name)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new DomainException("OwnerId is required.");
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Video name is required.");
        if (name.Length > 200)
            throw new DomainException("Video name must be 200 chars or fewer.");
    }
}
