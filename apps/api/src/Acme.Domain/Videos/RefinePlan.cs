using Acme.Domain.Common;

namespace Acme.Domain.Videos;

/// <summary>
/// What a refine will do, shown to the pharmacist before it runs.
/// </summary>
/// <remarks>
/// <para>
/// A refine is routed by classifying the pharmacist's own sentence, and
/// classification can be wrong in two directions that cost different things.
/// Too early and a caption fix rewrites the script and bills three providers.
/// Too late and a request to "make it friendlier" re-cuts identical footage,
/// reports success, and hands back a video that is unchanged in every way the
/// pharmacist cared about.
/// </para>
/// <para>
/// Neither is acceptable as a silent outcome, so nothing runs on the strength
/// of a classification alone. The plan is stored against the video, shown, and
/// only acted on when the pharmacist agrees to <i>that</i> plan.
/// </para>
/// <para>
/// <b>Stored rather than re-derived on confirmation.</b> Re-classifying would
/// mean a second model call that could answer differently from the one the
/// pharmacist was shown — and the case where it differs is exactly the case
/// where the difference matters. Storing it makes "what was agreed" a fact
/// rather than a repeated guess.
/// </para>
/// </remarks>
public sealed record RefinePlan
{
    /// <summary>The pharmacist's own words, as typed.</summary>
    public required string Instruction { get; init; }

    /// <summary>Where the pipeline would re-enter.</summary>
    public required RefineStage Stage { get; init; }

    /// <summary>What the pharmacist was told would change.</summary>
    public required string Summary { get; init; }

    /// <summary>When the plan was made.</summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>
    /// True once the pharmacist agreed to this plan and it was queued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carried on the plan rather than inferred from the video's status, even
    /// though a queued refine is always on a <c>Pending</c> video. The render
    /// worker sees only the document, and asking it to reconstruct "was this
    /// agreed to?" from a status it is simultaneously using to claim rows is
    /// the kind of derived agreement this codebase keeps getting wrong.
    /// </para>
    /// <para>
    /// It stays true after the render finishes, which is deliberate: a refine
    /// that failed and is retried should retry <i>the refine</i>, not silently
    /// fall back to re-rendering the original. Retry cannot reach a video that
    /// succeeded, so there is no path where a stale true re-applies something
    /// already applied.
    /// </para>
    /// </remarks>
    public bool Confirmed { get; init; }

    /// <summary>
    /// This refine exists to pin the pharmacy's own pictures, not to answer a
    /// sentence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The worker refuses any stage outside <c>FREE_STAGES</c> — deliberately,
    /// as the guard between a routing mistake and an unbudgeted bill. This
    /// refine re-enters at <c>visuals</c>, which is outside that set, so without
    /// a way to tell the two apart the worker would be right to refuse it.
    /// </para>
    /// <para>
    /// A flag rather than a sixth stage: the pipeline really does re-enter at
    /// visuals and runs exactly the same node. What differs is how it got there
    /// — a file rather than a classified sentence — and that is what decides
    /// whether it may spend. <c>RefineStageContractTests</c> keeps this in step
    /// with the Python side, as it does for the stage names themselves.
    /// </para>
    /// </remarks>
    public bool PinsOwnPictures { get; init; }

    /// <summary>
    /// This refine changes which footage appears at particular moments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Re-enters at <c>visuals</c> like <see cref="PinsOwnPictures"/>, and free
    /// for the same reason: it re-searches Pexels, reuses the stored script and
    /// narration, and calls no generation provider. The one model call it makes
    /// is the interpretation of the sentence — which is exactly what an editor
    /// refine already costs, and that lane has always been free.
    /// </para>
    /// <para>
    /// A second flag rather than a widening of the first, because the two are
    /// different operations that happen to share a lane: one pins a picture the
    /// pharmacy supplied, the other re-searches stock. Folding them together
    /// would mean the worker could not tell which it was being asked to do.
    /// </para>
    /// </remarks>
    public bool EditsShots { get; init; }

    /// <summary>How many pictures this refine appended to the video's list.</summary>
    /// <remarks>
    /// <see cref="Video.AddPictures"/> appends before the re-cut runs, so the
    /// worker can pin them. If that re-cut is then refused or fails, the
    /// pictures were never shown — and left on the list, every later refine
    /// planned them onto the first and last moments and refused to change
    /// "your own photo" in a video that showed stock there. The worker takes
    /// this many back off the end when the change does not happen.
    /// </remarks>
    public int AddedPictures { get; init; }

    /// <summary>The shots the pharmacist tapped, when this plan came from the picker.</summary>
    /// <remarks>
    /// Read by the worker as <c>refine.shotEdits</c> and applied as given: the
    /// shot is the one they chose, never one inferred from their words.
    /// Empty for every other kind of refine.
    /// </remarks>
    public IReadOnlyList<ShotChoice> ChosenShots { get; init; } = Array.Empty<ShotChoice>();

    /// <summary>The same plan, agreed to.</summary>
    public RefinePlan Confirm() => this with { Confirmed = true };

    /// <summary>
    /// How long a shown plan stays good for.
    /// </summary>
    /// <remarks>
    /// Not a security control — the plan is bound to one video and one owner
    /// already. It is here so a plan made against a video, left in a tab
    /// overnight and confirmed the next morning cannot act on a video that has
    /// since been retried, renamed or re-rendered into something else.
    /// </remarks>
    public static readonly TimeSpan Validity = TimeSpan.FromHours(2);

    public bool HasExpired(DateTime nowUtc) => nowUtc - CreatedAtUtc > Validity;

    /// <summary>
    /// True when <paramref name="instruction"/> is the one this plan describes.
    /// </summary>
    /// <remarks>
    /// Compared so a client cannot be shown the plan for one sentence and
    /// confirm a different one — whether by a genuine race between two tabs or
    /// by a request assembled by hand. Trimmed and case-insensitive, because
    /// the alternative is refusing a pharmacist for capitalising a word between
    /// seeing the plan and pressing the button.
    /// </remarks>
    public bool Describes(string? instruction) =>
        string.Equals(
            (instruction ?? string.Empty).Trim(),
            Instruction.Trim(),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why this instruction cannot be planned at all, or null if it can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separated from <see cref="Create"/> so it can be asked <b>before</b> the
    /// classifier runs. It used to be reachable only afterwards, and the effect
    /// was that neither of these sentences was ever said: ai-service declares
    /// <c>Field(min_length=1, max_length=1000)</c> on the same value, so an
    /// empty or over-long instruction came back as a 422, which
    /// <c>AiRefineClassifier</c> can only report as "we could not work out what
    /// to change just now".
    /// </para>
    /// <para>
    /// That is the same fault as the missing bearer token this feature already
    /// shipped once: a validation failure wearing the costume of a
    /// comprehension failure. The pharmacist is told the product did not
    /// understand them, when the truth is that they typed nothing, or typed
    /// three requests at once — both of which they could act on if anybody said
    /// so. Asking here also spares a round trip to a service that was always
    /// going to refuse.
    /// </para>
    /// </remarks>
    public static string? Unplannable(string? instruction)
    {
        var text = (instruction ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "Say what you would like changed.";
        if (text.Length > MaxInstructionLength)
            return $"Keep the change to {MaxInstructionLength} characters or fewer. "
                + "One change at a time works best.";
        return null;
    }

    /// <summary>
    /// A plan to re-cut the video with pictures the pharmacist just attached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built here rather than through <see cref="Create"/> because that one
    /// refuses an empty instruction, and this refine has no sentence behind it
    /// — attaching a photograph says what it wants unambiguously. The
    /// instruction is written for the pharmacist reading their video's history
    /// later, not parsed by anything.
    /// </para>
    /// <para>
    /// It is deliberately not left blank. <c>Instruction</c> is what the
    /// pharmacist is shown when they ask what happened to this video, and "" is
    /// a worse answer than a sentence.
    /// </para>
    /// </remarks>
    /// <summary>A plan to change what is shown at particular moments.</summary>
    /// <remarks>
    /// Built from the pharmacist's own sentence, which is kept verbatim: it is
    /// what they will be shown when they confirm, and what they will recognise
    /// later when they ask what happened to this video.
    /// </remarks>
    public static RefinePlan ForShotEdits(string instruction, DateTime nowUtc)
    {
        var text = (instruction ?? string.Empty).Trim();
        if (Unplannable(text) is { } refusal) throw new DomainException(refusal);

        return new RefinePlan
        {
            Instruction = text,
            Stage = RefineStage.Visuals,
            Summary = RefineStages.DescribeShotEdits(),
            CreatedAtUtc = nowUtc,
            EditsShots = true,
        };
    }

    /// <summary>A plan for shots the pharmacist tapped in the picker.</summary>
    /// <remarks>
    /// No preview and no confirmation, like <see cref="ForPictures"/>: tapping a
    /// tile and saying what goes there is already the pharmacist's decision, and
    /// nothing was interpreted that they would need to check. Free for the same
    /// reason as any shot edit: Pexels plus the stored voice.
    /// </remarks>
    public static RefinePlan ForChosenShots(IReadOnlyList<ShotChoice> shots, DateTime nowUtc)
    {
        if (shots is null || shots.Count == 0)
            throw new DomainException("Tap at least one shot to change.");
        if (shots.Count > ShotChoice.MaxPerRequest)
            throw new DomainException(
                $"Change up to {ShotChoice.MaxPerRequest} shots at a time.");
        if (shots.Select(s => s.Index).Distinct().Count() != shots.Count)
            throw new DomainException("Each shot can only be changed once per request.");

        var described = string.Join("; ", shots.Select(s =>
            s.Show.Length == 0 ? $"shot {s.Index + 1}" : $"shot {s.Index + 1} → {s.Show}"));

        return new RefinePlan
        {
            // Written for the pharmacist reading this video's history, not
            // parsed: the worker reads ChosenShots.
            Instruction = $"Change {described}",
            Stage = RefineStage.Visuals,
            Summary = RefineStages.DescribeShotEdits(),
            CreatedAtUtc = nowUtc,
            EditsShots = true,
            ChosenShots = shots,
        };
    }

    public static RefinePlan ForPictures(int pictureCount, DateTime nowUtc)
    {
        if (pictureCount <= 0)
            throw new DomainException("Choose at least one picture to add.");

        var noun = pictureCount == 1 ? "picture" : $"{pictureCount} pictures";
        return new RefinePlan
        {
            Instruction = $"Add my {noun} to this video",
            Stage = RefineStage.Visuals,
            Summary = RefineStages.DescribePictures(pictureCount),
            CreatedAtUtc = nowUtc,
            PinsOwnPictures = true,
            AddedPictures = pictureCount,
        };
    }

    public static RefinePlan Create(
        string instruction, RefineStage stage, string summary, DateTime nowUtc)
    {
        var text = (instruction ?? string.Empty).Trim();
        if (Unplannable(text) is { } refusal) throw new DomainException(refusal);

        return new RefinePlan
        {
            Instruction = text,
            Stage = stage,
            Summary = (summary ?? string.Empty).Trim(),
            CreatedAtUtc = nowUtc,
        };
    }

    /// <summary>
    /// Matches the classifier's own ceiling.
    /// </summary>
    /// <remarks>
    /// Above this the classifier stops trying and asks for one change at a
    /// time, because a long instruction is usually several requests and routing
    /// the whole thing to one stage satisfies part of it and silently drops the
    /// rest. Rejecting here as well means the pharmacist is told plainly
    /// instead of being handed a partial result.
    /// </remarks>
    public const int MaxInstructionLength = 1000;
}
