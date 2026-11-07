namespace Acme.Domain.Videos;

/// <summary>
/// Where a refine re-enters the render pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline is linear — researcher → scriptwriter → voice → visuals →
/// editor — and a refine restarts at the earliest stage its change actually
/// touches, reusing what the run before produced. That is the whole feature:
/// without artifact reuse every refine costs what a new video costs, and
/// "refine" is a misleading name for it.
/// </para>
/// <para>
/// The numbers are the pipeline order, so "earlier than" is a comparison and
/// not a lookup table that can disagree with itself.
/// </para>
/// </remarks>
public enum RefineStage
{
    /// <summary>Re-research the topic. Redoes everything after it.</summary>
    Researcher = 0,

    /// <summary>Rewrite the narration. Forces voice, then the editor.</summary>
    Scriptwriter = 1,

    /// <summary>Re-synthesise speech from the existing script.</summary>
    Voice = 2,

    /// <summary>Fetch different footage or imagery for the existing script.</summary>
    Visuals = 3,

    /// <summary>
    /// Re-cut only: captions, overlays, timing, the branded end card.
    /// </summary>
    Editor = 4,
}

/// <summary>
/// What each stage costs, and whether a pharmacist may reach it yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is the authority, and it is deliberately in the domain.</b> The
/// Python render worker maps a stage to a graph entry node and nothing else —
/// it does not decide whether a stage is allowed, and it does not decide
/// whether one spends money. Two processes agreeing on a money question by hand
/// is how this codebase has produced most of its faults.
/// </para>
/// <para>
/// <b>Why the paid stages are switched off.</b> `DECISION-video-brand-and-refine.md`
/// sets three things that have to be measured before a charge policy can be
/// anything but a guess: what Replicate and ElevenLabs actually cost per video,
/// how long an editor-only re-encode really takes, and how often refines reach
/// the scriptwriter at all. ElevenLabs is not metered, so the first of those
/// does not exist. Shipping the paid lanes now would mean either refines are
/// free and unbounded, or priced on a number nobody has.
/// </para>
/// <para>
/// So the staging machinery is built and tested in full, and the lanes that
/// spend are held closed until the meters exist. Turning them on is a change to
/// <see cref="Spends"/>'s companion below and a plan for what to charge — not a
/// piece of engineering.
/// </para>
/// </remarks>
public static class RefineStages
{
    /// <summary>The stage a refine restarts at when nothing earlier is needed.</summary>
    public const RefineStage Cheapest = RefineStage.Editor;

    /// <summary>
    /// True when re-running this stage calls a paid third party.
    /// </summary>
    /// <remarks>
    /// Researcher pays Tavily, scriptwriter pays Anthropic, voice pays
    /// ElevenLabs on a premium voice, visuals pays Replicate for AI imagery.
    /// The editor is ffmpeg on our own CPU and calls nobody.
    /// </remarks>
    public static bool Spends(RefineStage stage) => stage != RefineStage.Editor;

    /// <summary>
    /// True when a pharmacist may currently ask for this stage.
    /// </summary>
    /// <remarks>
    /// Enablement is checked on the .NET side before a refine is queued, and
    /// again by the worker before it runs. Belt and braces on purpose: this is
    /// the check that stands between a misrouted instruction and an unbudgeted
    /// bill, and it is cheap.
    /// </remarks>
    public static bool IsEnabled(RefineStage stage) => !Spends(stage);

    /// <summary>Plain English, for a pharmacist deciding whether to go ahead.</summary>
    /// <remarks>
    /// Phrased as what will be redone rather than which node runs. "Re-enters at
    /// the scriptwriter" means nothing to a pharmacist; "rewrite the narration,
    /// which means recording it again" is a decision they can make.
    /// </remarks>
    public static string Describe(RefineStage stage) => stage switch
    {
        RefineStage.Researcher =>
            "Look the subject up again and rebuild the video from scratch. "
            + "Everything changes, including the narration and the footage.",
        RefineStage.Scriptwriter =>
            "Rewrite what is said, then record the narration again. "
            + "The footage is kept.",
        RefineStage.Voice =>
            "Record the narration again from the same words. "
            + "Nothing else changes.",
        RefineStage.Visuals =>
            "Find different footage for the same narration. "
            + "The words and the voice are kept.",
        RefineStage.Editor =>
            "Re-cut the existing narration and footage — captions, overlays, "
            + "timing and the closing card. Takes seconds and costs nothing.",
        _ => "Unknown change.",
    };

    /// <summary>What re-cutting with newly attached pictures will do.</summary>
    /// <remarks>
    /// Says what is kept as well as what changes, because the thing a
    /// pharmacist is most likely to fear here is losing the narration they
    /// already approved.
    /// </remarks>
    public static string DescribePictures(int pictureCount) =>
        $"Put {(pictureCount == 1 ? "your picture" : $"your {pictureCount} pictures")} "
        + "into the video, spread across it. The words, the voice and the rest of "
        + "the footage are kept, and this costs nothing.";

    /// <summary>What re-filling moments with different footage will do.</summary>
    public static string DescribeShotEdits() =>
        "Find different footage for the shot you named and re-cut the video. "
        + "The words, the voice and the length stay exactly as they are, and "
        + "this costs nothing. The shot has to be named by position — \"the "
        + "last shot\", \"shot 2\" — or tapped in the list above; we don't pick "
        + "one from a description of what is in it.";

    /// <summary>
    /// Whether a video of this kind may have its moments re-filled, or the
    /// pharmacy's own pictures pinned into it, after the fact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A separate door, not a hole in <see cref="Spends"/>.</b> Attaching a
    /// photograph re-enters at <see cref="RefineStage.Visuals"/>, which
    /// <see cref="Spends"/> calls expensive — and for a text instruction it is,
    /// because "find me different footage" is an open-ended request. But the
    /// cost of that stage is not the stage: it is the Replicate call inside it.
    /// A pharmacist pinning their own shopfront calls no model at all. The
    /// script and the narration are reused from storage, the stock footage is
    /// re-fetched from Pexels, which is free, and the pictures are theirs
    /// already.
    /// </para>
    /// <para>
    /// So this is asked as its own question rather than by widening
    /// <see cref="Spends"/>. Widening it would have opened "find different
    /// footage" as a side effect — the classifier routes there from a sentence,
    /// and nothing would have stopped it. Nothing routes here from a sentence:
    /// a picture arrives as a file, and a file cannot be classified into the
    /// wrong lane.
    /// </para>
    /// <para>
    /// <b>AI-imagery videos are refused.</b> Re-entering visuals on one
    /// regenerates every beat through Replicate at roughly five cents, so it
    /// would spend after all. The stills are already stored against
    /// <c>stillBlobKeys</c>, so the day the visuals node learns to reuse them
    /// this becomes free too — and this method is the one place that would
    /// change.
    /// </para>
    /// </remarks>
    public static bool PicturesMayBePinned(VideoVisualStyle style) =>
        style != VideoVisualStyle.AiImagery;

    /// <summary>
    /// Whether this video's footage may be re-searched without spending.
    /// </summary>
    /// <remarks>
    /// The same question as <see cref="PicturesMayBePinned"/> and the same
    /// answer, for the same reason — re-entering visuals on an AI-imagery video
    /// regenerates every beat through Replicate. Named separately because the
    /// two operations are different, and a caller asking about one should not
    /// have to know it is really asking about the other.
    /// </remarks>
    public static bool ShotsMayBeRefilled(VideoVisualStyle style) =>
        style != VideoVisualStyle.AiImagery;

    /// <summary>Why pictures cannot be added to this particular video.</summary>
    public static string WhyPicturesRefused(VideoVisualStyle style) =>
        PicturesMayBePinned(style)
            ? throw new InvalidOperationException(
                "Asked why pictures were refused for a video that allows them.")
            : "This video was made with AI-generated imagery, and adding your own "
              + "pictures to it would mean generating all of its other pictures "
              + "again — which your plan pays for. Create a new video with your "
              + "photographs attached instead.";

    /// <summary>
    /// Why a stage is unavailable, in terms a pharmacist can act on.
    /// </summary>
    /// <remarks>
    /// Never "not implemented". The machinery works; what is missing is our own
    /// answer to what it should cost, and saying so honestly is better than
    /// implying the product is broken.
    /// </remarks>
    public static string ExplainUnavailable(RefineStage stage) =>
        $"Refining this video needs us to {Lowered(Describe(stage))} "
        + "That uses the generation services your plan pays for, and we have not "
        + "opened it up yet. For now, create a new video — or ask for a change to "
        + "the captions, overlays, timing or closing card, which we can do "
        + "straight away.";

    private static string Lowered(string sentence) =>
        sentence.Length == 0 ? sentence : char.ToLowerInvariant(sentence[0]) + sentence[1..];

    /// <summary>Parses the stage name the classifier returned.</summary>
    /// <remarks>
    /// Case-insensitive and strict: anything unrecognised is null rather than a
    /// default. Defaulting an unparseable answer to the cheapest stage would
    /// quietly run an editor pass for a request the editor cannot satisfy, and
    /// defaulting it to the earliest would quietly spend money. Neither is
    /// something to guess at, so the caller is made to handle it.
    /// </remarks>
    public static RefineStage? Parse(string? name) =>
        Enum.TryParse<RefineStage>(name?.Trim(), ignoreCase: true, out var stage)
        && Enum.IsDefined(stage)
            ? stage
            : null;
}
