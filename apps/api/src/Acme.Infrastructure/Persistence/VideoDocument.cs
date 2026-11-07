using MongoDB.Bson.Serialization.Attributes;

namespace Acme.Infrastructure.Persistence;

/// <summary>Where one piece of stock footage came from.</summary>
/// <remarks>
/// Both the link and the id. The link is what a refine fetches; the id is the
/// fallback, because a CDN address can move and a Pexels video id can always be
/// looked up again through the API.
/// </remarks>
internal sealed class ClipSourceDocument
{
    [BsonElement("path")]      public string? Path     { get; set; }
    [BsonElement("pexels_id")] public string? PexelsId { get; set; }
    [BsonElement("link")]      public string? Link     { get; set; }
    [BsonElement("query")]     public string? Query    { get; set; }

    // Written by the worker (beats.record_by_beat); read here only to build the
    // shot picker. .NET never writes clipSources — see MongoVideoRepository.
    [BsonElement("segment")]   public int?    Segment  { get; set; }
    [BsonElement("role")]      public string? Role     { get; set; }
    [BsonElement("shows")]     public string? Shows    { get; set; }
}

/// <summary>When one moment was on screen, as the worker rendered it.</summary>
/// <remarks>Worker-owned and read-only here, like <see cref="ClipSourceDocument"/>.</remarks>
internal sealed class ShotTimelineDocument
{
    [BsonElement("segment")]  public int?    Segment  { get; set; }
    [BsonElement("start")]    public double  Start    { get; set; }
    [BsonElement("end")]      public double  End      { get; set; }
    [BsonElement("thumbKey")] public string? ThumbKey { get; set; }
}

/// <summary>A shot the pharmacist tapped. Read by the worker as <c>refine.shotEdits</c>.</summary>
internal sealed class ShotEditDocument
{
    [BsonElement("index")] public int     Index { get; set; }
    [BsonElement("show")]  public string? Show  { get; set; }
}

/// <summary>
/// The pharmacy's identity as stamped on one video attempt.
/// </summary>
/// <remarks>
/// A flat copy rather than a reference to the brand kit, because the point is
/// that it does <i>not</i> move under a render that is already in flight. The
/// .NET side re-resolves it on Retry; the Python worker only ever reads it.
/// </remarks>
internal sealed class VideoBrandDocument
{
    [BsonElement("name")]        public string? Name       { get; set; }
    [BsonElement("address")]     public string? Address    { get; set; }
    [BsonElement("phone")]       public string? Phone      { get; set; }
    [BsonElement("bookingUrl")]  public string? BookingUrl { get; set; }

    /// <summary>Storage key, not a URL — the worker reads the blob directly.</summary>
    [BsonElement("logoBlobKey")] public string? LogoBlobKey { get; set; }

    [BsonElement("primaryColor")]   public string? PrimaryColor   { get; set; }
    [BsonElement("secondaryColor")] public string? SecondaryColor { get; set; }
    [BsonElement("accentColor")]    public string? AccentColor    { get; set; }

    [BsonElement("licenseNumber")]    public string? LicenseNumber    { get; set; }
    [BsonElement("regulatoryFooter")] public string? RegulatoryFooter { get; set; }

    /// <summary>
    /// Whether a real pharmacy name was found when this was resolved.
    /// </summary>
    /// <remarks>
    /// Stored rather than recomputed from <see cref="Name"/>, which falls back
    /// to the word "Pharmacy" — a pharmacy genuinely trading under that name
    /// would otherwise be read as having no name at all, and silently lose its
    /// branding.
    /// </remarks>
    [BsonElement("usable")] public bool Usable { get; set; }
}

/// <summary>The change a pharmacist asked for, and what it was said it would do.</summary>
/// <remarks>
/// Written by the .NET side and read by the Python worker, which uses `stage`
/// to decide where to re-enter the pipeline and `options` to know what the
/// editor should actually change. The worker never writes it back.
/// </remarks>
internal sealed class VideoRefineDocument
{
    [BsonElement("instruction")] public string? Instruction { get; set; }

    /// <summary>The pipeline stage name, lowercase — "editor", "visuals", …</summary>
    /// <remarks>
    /// A name rather than the enum's number. The worker reads this, and a
    /// number would mean the two sides agreeing on an ordering by hand, where a
    /// name is wrong loudly instead of quietly.
    /// </remarks>
    [BsonElement("stage")] public string? Stage { get; set; }

    /// <summary>
    /// This refine pins the pharmacist's own pictures rather than answering a
    /// sentence, so the worker may run it even though its stage is not free.
    /// </summary>
    /// <remarks>
    /// Read by the Python worker, which otherwise refuses any stage outside
    /// <c>FREE_STAGES</c>. Absent on every refine written before this existed,
    /// and absent reads as false on both sides — which is the safe direction:
    /// an old row stays refused rather than becoming unexpectedly runnable.
    /// </remarks>
    [BsonElement("pinsOwnPictures")] public bool PinsOwnPictures { get; set; }

    /// <summary>
    /// This refine re-fills particular moments with different stock footage.
    /// </summary>
    /// <remarks>
    /// Read by the worker for the same reason as <c>pinsOwnPictures</c>: both
    /// re-enter at a stage outside <c>FREE_STAGES</c> without spending, and the
    /// worker cannot tell that from the stage alone. Absent reads as false on
    /// both sides, which is the safe direction for an older row.
    /// </remarks>
    [BsonElement("editsShots")] public bool EditsShots { get; set; }

    /// <summary>Pictures appended by this refine; read by the worker to undo them
    /// if the re-cut does not happen. See <c>RefinePlan.AddedPictures</c>.</summary>
    [BsonElement("addedPictures")] public int AddedPictures { get; set; }

    /// <summary>Shots tapped in the picker, applied by the worker as given.</summary>
    [BsonElement("shotEdits")] [BsonIgnoreIfNull]
    public List<ShotEditDocument>? ShotEdits { get; set; }

    [BsonElement("summary")]       public string? Summary { get; set; }
    [BsonElement("createdAtUtc")]  public DateTime CreatedAtUtc { get; set; }

    /// <summary>True once the pharmacist agreed and this was queued.</summary>
    /// <remarks>
    /// A plan that was only ever shown is kept too — it is what the
    /// confirmation is checked against — so the two states have to be
    /// distinguishable. Without this the worker could not tell a proposal from
    /// an instruction.
    /// </remarks>
    [BsonElement("confirmed")] public bool Confirmed { get; set; }
}

/// <summary>One picture the pharmacy attached to a video.</summary>
/// <remarks>
/// Its own type rather than a bare string so the source survives the round
/// trip: a logo and an uploaded photograph are cropped the same way but mean
/// different things, and the worker logs which it used.
/// </remarks>
internal sealed class VideoImageDocument
{
    [BsonElement("blobKey")] public string BlobKey { get; set; } = default!;

    /// <summary>"upload", "logo" or "poster".</summary>
    /// <remarks>
    /// A string, not the enum's integer. This field is read by the Python
    /// worker, and an integer would make the two sides agree by position —
    /// reordering the enum here would silently change what a stored row means.
    /// </remarks>
    [BsonElement("source")] public string Source { get; set; } = "upload";

    [BsonElement("description")] [BsonIgnoreIfNull]
    public string? Description { get; set; }

    /// <summary>
    /// Which part of the picture survives the 9:16 crop, 0–1, centre by default.
    /// </summary>
    /// <remarks>
    /// Not <c>[BsonIgnoreIfNull]</c>: these are plain doubles, so an absent
    /// field reads as 0 rather than as "unset" — which would silently reframe
    /// every picture attached before this existed to the top-left corner. The
    /// mapper treats a missing pair as centre instead.
    /// </remarks>
    [BsonElement("focalX")] public double? FocalX { get; set; }
    [BsonElement("focalY")] public double? FocalY { get; set; }

    /// <summary>1.0 when absent — see <c>VideoImage.Zoom</c>. Nullable for the
    /// same reason as the focal point: a plain double reads a missing field as
    /// 0, which would clamp to 0.5 and shrink every older photo to half size.</summary>
    [BsonElement("zoom")] public double? Zoom { get; set; }
}

internal sealed class VideoDocument
{
    [BsonId] public string Id { get; set; } = default!;

    [BsonElement("ownerId")]      public string OwnerId { get; set; } = default!;
    [BsonElement("name")]         public string Name    { get; set; } = default!;
    [BsonElement("topic")]        public string Topic   { get; set; } = default!;
    [BsonElement("styleTone")]    public string? StyleTone { get; set; }

    [BsonElement("source")]       public int Source { get; set; }
    [BsonElement("status")]       public int Status { get; set; }

    [BsonElement("scriptSource")] public int ScriptSource { get; set; }
    [BsonElement("visualStyle")]  public int VisualStyle  { get; set; }
    [BsonElement("description")]  public string? Description  { get; set; }
    [BsonElement("customScript")] public string? CustomScript { get; set; }
    [BsonElement("voiceId")]      public string? VoiceId      { get; set; }

    /// <summary>
    /// The pharmacy's own pictures, chosen when the video was queued.
    /// </summary>
    /// <remarks>
    /// <b>.NET-owned, and read by the Python worker.</b> Written here at create
    /// and on retry; the worker only reads it, the same direction as
    /// <c>brand</c> and <c>refine</c>. <c>[BsonIgnoreIfNull]</c> for the reason
    /// every other nullable field on this document has it — an explicitly null
    /// array is not the same thing to Mongo as an absent one, and the worker
    /// reads this with <c>.get()</c> either way.
    /// </remarks>
    [BsonElement("images")] [BsonIgnoreIfNull]
    public List<VideoImageDocument>? Images { get; set; }

    [BsonElement("aspect")]       public string Aspect { get; set; } = "9:16";
    [BsonElement("duration")]     public int DurationSeconds { get; set; }

    [BsonElement("blobKey")]          public string? BlobKey { get; set; }
    [BsonElement("thumbnailBlobKey")] public string? ThumbnailBlobKey { get; set; }
    [BsonElement("note")]             public string? Note    { get; set; }

    /// <summary>
    /// Why a render failed, in technical terms. Worker-written, never by this side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>note</c> is the pharmacist's sentence; this is the operator's. They
    /// exist separately because making one field serve both means either a
    /// pharmacist reading "TypeError: expected string or bytes-like object" or
    /// an engineer reading "something went wrong" — and this project has now
    /// shipped both of those mistakes.
    /// </para>
    /// <para>
    /// Deliberately absent from <see cref="MongoVideoRepository.UpdateAsync"/>'s
    /// field list. The Python worker owns it; including it here would have the
    /// .NET side overwrite the worker's explanation with null on the next
    /// unrelated save.
    /// </para>
    /// </remarks>
    [BsonElement("diagnostic")]
    [BsonIgnoreIfNull]
    public string? Diagnostic { get; set; }

    // The render worker's claim. Absent on every row written before leases
    // existed, which is why both are nullable rather than defaulted — a missing
    // lease means "nobody holds this", and on a Rendering row that is the
    // correct reading: it was stranded by a worker that is long gone.
    //
    // Written by the Python worker (apps/ai-service/app/video_agent/worker.py),
    // read by the .NET side so Retry can tell a live render from a dead one.
    [BsonElement("leaseOwner")]        public string? LeaseOwner { get; set; }
    [BsonElement("leaseExpiresAtUtc")] public DateTime? LeaseExpiresAtUtc { get; set; }

    // Written by the .NET side at create and again on Retry, read by the Python
    // worker at claim time. Null on every row queued before branding existed,
    // which renders exactly as it always did — no migration needed.
    [BsonElement("brand")] public VideoBrandDocument? Brand { get; set; }

    // The change asked for. Written by the .NET side only; the worker reads it
    // to decide where to re-enter the pipeline and never writes it back.
    [BsonElement("refine")] public VideoRefineDocument? Refine { get; set; }

    // Fields the Python worker writes back on Ready — tolerated extras so
    // the mapper passes them through if surfaced later. Currently unused
    // by the .NET DTO but available in Mongo for debugging / audit.
    //
    // <b>[BsonIgnoreIfNull] on every one, the same rule as the usage fields
    // below.</b> Today only `usageAttempts` is $push-ed, so an explicit null in
    // `title` or `script` is harmless — these are plain $set. The attribute is
    // here anyway for two reasons. `tags`, `clipSources` and `stillBlobKeys` are
    // arrays, and the day anything appends to one of them rather than replacing
    // it, an explicit null reproduces the freeze exactly: $push refuses a null
    // field and creates the array happily when it is absent. And a uniform rule
    // over worker-owned fields is one somebody can apply without re-deriving
    // which Mongo operator will touch which field — which is the judgement that
    // was got wrong the first time.
    [BsonElement("title")] [BsonIgnoreIfNull]
    public string? GeneratedTitle { get; set; }

    [BsonElement("script")] [BsonIgnoreIfNull]
    public string? Script { get; set; }

    [BsonElement("generatedDescription")] [BsonIgnoreIfNull]
    public string? GeneratedDescription { get; set; }

    [BsonElement("tags")] [BsonIgnoreIfNull]
    public List<string>? Tags { get; set; }

    [BsonElement("metadataJson")] [BsonIgnoreIfNull]
    public string? MetadataJson { get; set; }

    [BsonElement("aiImageCostUsd")] [BsonIgnoreIfNull]
    public double? AiImageCostUsd { get; set; }

    [BsonElement("aiImagesGenerated")] [BsonIgnoreIfNull]
    public int? AiImagesGenerated { get; set; }

    // What a refine resumes from. Written by the worker, never by this side.
    //
    // The narration is kept as bytes because regenerating it costs ElevenLabs
    // money on a premium voice. The stock footage is kept only as references —
    // twelve HD clips is roughly 120 MB a video, and the footage is public and
    // free to fetch again, so storing the bytes would be paying a large upload
    // on every render for something most videos never use.
    /// <summary>
    /// What the render consumed, written by the Python worker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Untyped on this side deliberately. The shape belongs to the worker —
    /// which providers, which units, which rates were configured at the time —
    /// and mirroring it here would be a second definition to keep in step for
    /// no gain, since nothing in the domain makes decisions from it yet.
    /// </para>
    /// <para>
    /// <b>Worker-owned, so this side must never write it.</b> It records
    /// characters synthesised, tokens spent and seconds encoding — the
    /// measurements the refine charge policy is waiting on. An update from here
    /// that included it would let renaming a video erase the evidence, which is
    /// the fault this repository was rewritten to stop.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <b>[BsonIgnoreIfNull] on all four worker-owned fields, and it is not
    /// cosmetic.</b> `UpdateAsync` correctly never writes them — but `AddAsync`
    /// inserts the whole document, so a freshly created video carried
    /// <c>usage: null</c>, <c>usageAttempts: null</c> and the rest.
    ///
    /// MongoDB's <c>$push</c> refuses a field that is explicitly null: "The
    /// field 'usageAttempts' must be an array but is of type null". It creates
    /// the array happily when the field is <i>absent</i>. So the worker's very
    /// first usage write failed on every new video — and because that write is
    /// on the failure path too, a render that failed could not record that it
    /// had failed. The video stayed Rendering, was re-claimed, failed again,
    /// and never moved. "Freezes without progressing" was this.
    ///
    /// Absent and null are not the same thing to Mongo, and "this side must
    /// never write it" has to include writing it as null.
    /// </remarks>
    [BsonElement("usage")] [BsonIgnoreIfNull]
    public MongoDB.Bson.BsonDocument? Usage { get; set; }

    /// <summary>Each render attempt's usage, most recent last. Worker-owned.</summary>
    /// <remarks>
    /// Capped by the worker at the last twenty. A cost is read very differently
    /// depending on whether anything came of it, so each entry says which
    /// outcome it belongs to.
    /// </remarks>
    [BsonElement("usageAttempts")] [BsonIgnoreIfNull]
    public List<MongoDB.Bson.BsonDocument>? UsageAttempts { get; set; }

    /// <summary>
    /// Running totals across every attempt, incremented and never truncated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the number to read for "what has this video cost".</b>
    /// <see cref="Usage"/> is the most recent attempt alone, and
    /// <see cref="UsageAttempts"/> drops its oldest entries once a video has
    /// been retried enough — so both understate a video that was retried, which
    /// is exactly the set of videos that cost the most.
    /// </para>
    /// <para>
    /// Carries unit counts as well as money, because the rates are configured
    /// separately and may be set long after the fact. Units let the spend be
    /// computed retrospectively; a money total alone could not be corrected.
    /// </para>
    /// </remarks>
    [BsonElement("usageTotals")] [BsonIgnoreIfNull]
    public MongoDB.Bson.BsonDocument? UsageTotals { get; set; }

    /// <summary>
    /// The researched claims the script was written from. Worker-owned.
    /// </summary>
    /// <remarks>
    /// Kept so a refine that rewrites the wording does not have to re-research
    /// the subject — which would cost a search round and, worse, could come
    /// back with different facts, quietly changing what the video claims when
    /// somebody only asked for a friendlier tone. On patient-facing health
    /// copy, that is the difference between an edit and a new assertion.
    /// </remarks>
    [BsonElement("facts")] [BsonIgnoreIfNull]
    public List<MongoDB.Bson.BsonDocument>? Facts { get; set; }

    [BsonElement("audioBlobKey")] [BsonIgnoreIfNull]
    public string? AudioBlobKey { get; set; }

    [BsonElement("clipSources")] [BsonIgnoreIfNull]
    public List<ClipSourceDocument>? ClipSources { get; set; }

    /// <summary>Which moment was on screen when, as rendered. Worker-owned.</summary>
    [BsonElement("shotTimeline")] [BsonIgnoreIfNull]
    public List<ShotTimelineDocument>? ShotTimeline { get; set; }

    // AI-imagery jobs only. Empty on a stock-footage video, which is most of
    // them. Kept because Flux is stochastic — regenerating returns different
    // pictures, so a refine that only changed a caption would come back looking
    // like a different video.
    [BsonElement("stillBlobKeys")] [BsonIgnoreIfNull]
    public List<string>? StillBlobKeys { get; set; }

    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}
