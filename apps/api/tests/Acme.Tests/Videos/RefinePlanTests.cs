using Acme.Domain.Common;
using Acme.Domain.Videos;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Videos;

/// <summary>
/// The agreement between what a pharmacist was shown and what runs.
/// </summary>
/// <remarks>
/// <para>
/// A refine is routed by interpreting the pharmacist's own sentence, and that
/// interpretation can be wrong in two directions with different costs. Too
/// early and a caption fix rewrites the script and bills three providers. Too
/// late and "make it friendlier" re-cuts identical footage, reports success,
/// and returns a video unchanged in every way they cared about.
/// </para>
/// <para>
/// Neither may happen silently, so the plan is shown first and stored, and only
/// that plan can be acted on. These tests are the storage-and-agreement half;
/// whether the classifier routes well is a separate question that needs a real
/// model.
/// </para>
/// </remarks>
public class RefinePlanTests
{
    private static readonly DateTime Now = new(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);

    private static RefinePlan Plan(
        string instruction = "make the subtitles bigger",
        RefineStage stage = RefineStage.Editor) =>
        RefinePlan.Create(instruction, stage, RefineStages.Describe(stage), Now);

    private static Video Ready()
    {
        var v = Video.Create(
            ownerId: "owner-1", name: "Flu jabs", topic: "flu vaccination",
            scriptSource: VideoScriptSource.Ai, visualStyle: VideoVisualStyle.Realistic,
            description: null, customScript: null, voiceId: null, styleTone: null);
        Force(v, nameof(Video.Status), VideoStatus.Ready);
        return v;
    }

    private static void Force(object target, string name, object? value) =>
        target.GetType()
            .GetProperty(name, System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(target, value);

    /* ── what a plan is ──────────────────────────────────────────────── */

    [Fact]
    public void A_new_plan_is_not_yet_agreed_to()
    {
        Plan().Confirmed.Should().BeFalse(
            "a plan that has only been shown must be distinguishable from one "
            + "the pharmacist accepted — the worker sees only the document");
    }

    [Fact]
    public void Confirming_changes_nothing_except_that_it_was_agreed_to()
    {
        var shown = Plan();
        var agreed = shown.Confirm();

        agreed.Confirmed.Should().BeTrue();
        agreed.Should().Be(shown with { Confirmed = true },
            "confirming is an acknowledgement, not an opportunity to alter what "
            + "was agreed");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void An_empty_instruction_is_refused(string blank)
    {
        var act = () => RefinePlan.Create(blank, RefineStage.Editor, "x", Now);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void An_instruction_longer_than_the_classifier_accepts_is_refused()
    {
        // The classifier stops trying above this and asks for one change at a
        // time, because a long instruction is usually several requests and
        // routing them all to one stage satisfies part and drops the rest.
        // Rejecting here too means the pharmacist is told, not half-served.
        var act = () => RefinePlan.Create(
            new string('x', RefinePlan.MaxInstructionLength + 1),
            RefineStage.Editor, "x", Now);

        act.Should().Throw<DomainException>();
    }

    /* ── the plan is bound to the sentence it describes ──────────────── */

    [Fact]
    public void A_plan_describes_the_instruction_it_was_made_for()
    {
        Plan("make the subtitles bigger")
            .Describes("make the subtitles bigger").Should().BeTrue();
    }

    [Theory]
    [InlineData("  make the subtitles bigger  ")]
    [InlineData("Make The Subtitles Bigger")]
    public void Whitespace_and_case_do_not_break_the_binding(string variant)
    {
        // Refusing a pharmacist for capitalising a word between seeing the plan
        // and pressing the button would be a worse fault than the one this
        // check exists to prevent.
        Plan("make the subtitles bigger").Describes(variant).Should().BeTrue();
    }

    [Theory]
    [InlineData("rewrite the script to be friendlier")]
    [InlineData("make the subtitles smaller")]
    [InlineData("")]
    [InlineData(null)]
    public void A_different_instruction_is_not_the_plan_that_was_shown(string? other)
    {
        // The whole point of the confirmation. Without this a request could
        // carry one sentence past a plan made for another — and the difference
        // between those two sentences is the difference between a free re-cut
        // and a billable rewrite.
        Plan("make the subtitles bigger").Describes(other).Should().BeFalse();
    }

    [Fact]
    public void A_plan_goes_stale()
    {
        var plan = Plan();

        plan.HasExpired(Now.Add(RefinePlan.Validity).AddMinutes(-1)).Should().BeFalse();
        plan.HasExpired(Now.Add(RefinePlan.Validity).AddMinutes(1)).Should().BeTrue(
            "a plan made against a video and left in a tab overnight must not "
            + "act on a video that has since been retried or re-rendered");
    }

    /* ── what the video does with it ─────────────────────────────────── */

    [Fact]
    public void Proposing_records_the_plan_without_queueing_anything()
    {
        var v = Ready();
        v.ProposeRefine(Plan());

        v.Refine.Should().NotBeNull();
        v.Refine!.Confirmed.Should().BeFalse();
        v.Status.Should().Be(VideoStatus.Ready,
            "a preview must not start a render — the pharmacist has not agreed yet");
    }

    [Fact]
    public void Starting_queues_the_video_and_marks_the_plan_agreed()
    {
        var v = Ready();
        v.StartRefine(Plan());

        v.Status.Should().Be(VideoStatus.Pending,
            "the worker's existing claim takes Pending rows atomically, leases "
            + "them and recovers them when it dies — all of which a refine needs "
            + "exactly as much as a first render");
        v.Refine!.Confirmed.Should().BeTrue();
        v.LeaseOwner.Should().BeNull();
        v.LeaseExpiresAtUtc.Should().BeNull();
    }

    [Fact]
    public void Queueing_a_refine_keeps_the_video_the_pharmacist_already_has()
    {
        var v = Ready();
        Force(v, nameof(Video.BlobKey), "videos/owner-1/vid.mp4");
        Force(v, nameof(Video.ThumbnailBlobKey), "videos/owner-1/vid.jpg");

        v.StartRefine(Plan());

        v.BlobKey.Should().Be("videos/owner-1/vid.mp4",
            "they keep watching what they have while the new cut is made, and "
            + "if the refine fails they still have it");
        v.ThumbnailBlobKey.Should().NotBeNull();
    }

    [Theory]
    [InlineData(VideoStatus.Pending)]
    [InlineData(VideoStatus.Rendering)]
    [InlineData(VideoStatus.Failed)]
    public void A_video_that_is_not_finished_cannot_be_refined(VideoStatus status)
    {
        var v = Ready();
        Force(v, nameof(Video.Status), status);

        var propose = () => v.ProposeRefine(Plan());
        var start = () => v.StartRefine(Plan());

        propose.Should().Throw<DomainException>();
        start.Should().Throw<DomainException>();
    }

    [Fact]
    public void An_agreed_plan_stays_agreed_so_it_cannot_be_replayed()
    {
        // The confirm request carries the instruction, and the plan survives the
        // render that used it. Once the video is Ready again the same request
        // would re-queue the same work with no fresh preview — harmless while
        // only the free lane is open, and a way to re-run billable work on
        // repeat the moment one is not. The service refuses a plan already
        // marked agreed; this pins the flag it reads.
        var v = Ready();
        v.StartRefine(Plan());
        Force(v, nameof(Video.Status), VideoStatus.Ready);

        v.Refine!.Confirmed.Should().BeTrue(
            "the render finishing must not quietly reset this to a fresh, "
            + "re-confirmable plan");
    }

    [Fact]
    public void The_refusal_says_which_situation_it_is()
    {
        // Three different situations with three different next steps. "Cannot
        // be changed" for all of them tells a pharmacist nothing they can act
        // on, and support then has to ask which it was.
        var messages = new List<string>();
        foreach (var status in new[]
                 { VideoStatus.Pending, VideoStatus.Rendering, VideoStatus.Failed })
        {
            var v = Ready();
            Force(v, nameof(Video.Status), status);
            try { v.StartRefine(Plan()); }
            catch (DomainException ex) { messages.Add(ex.Message); }
        }

        messages.Should().HaveCount(3);
        messages.Should().OnlyHaveUniqueItems();
        messages.Should().NotContain(m => m.Contains("Pending") || m.Contains("Rendering"),
            "these are read by a pharmacist, not by us");
    }

    /* ── the paid lanes stay shut ────────────────────────────────────── */

    [Theory]
    [InlineData(RefineStage.Researcher)]
    [InlineData(RefineStage.Scriptwriter)]
    [InlineData(RefineStage.Voice)]
    [InlineData(RefineStage.Visuals)]
    public void A_plan_can_be_made_for_a_closed_stage_but_not_run(RefineStage stage)
    {
        // The domain will happily hold a plan for any stage — it is a record of
        // what was asked for. What stops it running is the enablement check in
        // the service, and that check exists in one place rather than being
        // spread across the entity.
        var plan = Plan("rewrite it", stage);

        plan.Stage.Should().Be(stage);
        RefineStages.IsEnabled(stage).Should().BeFalse(
            "spend is unmeasured, so {0} is closed — see the decision record", stage);
        RefineStages.ExplainUnavailable(stage).Should().NotContain(stage.ToString(),
            "the explanation is for a pharmacist, who does not know what a "
            + "{0} node is", stage);
    }

    [Fact]
    public void The_editor_stage_is_the_one_that_runs()
    {
        RefineStages.IsEnabled(RefineStage.Editor).Should().BeTrue();
        RefineStages.Spends(RefineStage.Editor).Should().BeFalse(
            "it is ffmpeg on our own CPU and calls nobody");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public void An_empty_instruction_is_refused_in_its_own_words(string? instruction)
    {
        // Refused *here*, which is the point — this is asked before the
        // classifier runs. ai-service declares `Field(min_length=1)` on the same
        // value, so an empty instruction that got that far came back a 422, and
        // AiRefineClassifier can only report a 422 as "we could not work out
        // what to change just now". The pharmacist was told the product had
        // misunderstood them when in fact nobody had looked.
        RefinePlan.Unplannable(instruction).Should().Be(
            "Say what you would like changed.",
            "an empty instruction has an obvious explanation and should get it");
    }

    [Fact]
    public void A_too_long_instruction_says_so_rather_than_shrugging()
    {
        var tooLong = new string('a', RefinePlan.MaxInstructionLength + 1);

        RefinePlan.Unplannable(tooLong).Should().Contain("One change at a time",
            "this is the sentence that tells the pharmacist what to do about it");
    }

    [Fact]
    public void Length_is_judged_after_trimming_so_whitespace_cannot_smuggle_a_refusal()
    {
        // The exact band that made this worth separating. Trailing whitespace on
        // a maximum-length instruction passes a trimmed check and fails
        // ai-service's `Field(max_length=...)`, which counts what it is sent —
        // so the service now trims once and passes the trimmed text on. If that
        // trim is ever removed, this pairing is what says why it was there.
        var padded = new string('a', RefinePlan.MaxInstructionLength) + "     ";

        RefinePlan.Unplannable(padded).Should().BeNull(
            "trailing spaces are not a change request");
        padded.Trim().Length.Should().Be(RefinePlan.MaxInstructionLength,
            "and what gets sent onward must be the trimmed text, or the check "
            + "above passes something the next service will refuse");
    }

    [Fact]
    public void A_plannable_instruction_is_not_refused()
    {
        RefinePlan.Unplannable("make the captions bigger").Should().BeNull();
        RefinePlan.Unplannable(new string('a', RefinePlan.MaxInstructionLength))
            .Should().BeNull("the limit is inclusive");
    }

    [Fact]
    public void Create_refuses_exactly_what_Unplannable_refuses()
    {
        // One definition, two callers. Before this was split out, the only way
        // to ask the question was to build a plan — which is why the check ran
        // after the classifier rather than before it.
        foreach (var bad in new[] { "", "   ", new string('a', RefinePlan.MaxInstructionLength + 1) })
        {
            var expected = RefinePlan.Unplannable(bad);
            expected.Should().NotBeNull();

            var act = () => RefinePlan.Create(
                bad, RefineStage.Editor, "summary", Now);

            act.Should().Throw<DomainException>().WithMessage(expected!,
                "Create and Unplannable must agree, or the pharmacist gets one "
                + "wording from the preview and another from the domain");
        }
    }
}
