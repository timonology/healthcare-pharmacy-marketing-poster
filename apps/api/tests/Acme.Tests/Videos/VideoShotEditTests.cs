using System.Text.RegularExpressions;
using Acme.Domain.Common;
using Acme.Domain.Videos;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Videos;

/// <summary>
/// Re-filling particular moments of a finished video, from a sentence.
/// </summary>
/// <remarks>
/// <para>
/// <b>This deliberately opens a lane the picture feature deliberately did not.</b>
/// When pinning the pharmacy's own photographs was built, the note against it
/// said widening <c>Spends</c> would open "find me different footage" as a side
/// effect — the classifier routes there from plain English and nothing would
/// have stopped it. That caution was about opening it <i>by accident</i>.
/// </para>
/// <para>
/// This opens it on purpose, for the one shape of the operation that costs
/// nothing: re-searching Pexels for moments the pharmacist named, reusing the
/// stored script and narration. What has to stay true is the boundary — an
/// AI-imagery video still regenerates every beat through Replicate, so it is
/// still refused, and no other stage becomes reachable.
/// </para>
/// </remarks>
public class VideoShotEditTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 9, 0, 0, DateTimeKind.Utc);

    private const string Instruction =
        "remove the man at the end and show an elderly couple walking in a park";

    // ── which videos may be re-cut ──────────────────────────────────────

    [Fact]
    public void A_stock_footage_video_may_have_its_moments_re_filled()
    {
        RefineStages.ShotsMayBeRefilled(VideoVisualStyle.Realistic).Should().BeTrue();
    }

    [Fact]
    public void An_ai_imagery_video_may_not()
    {
        // Re-entering visuals on one regenerates every beat through Replicate.
        // Free for a stock video, emphatically not for this one.
        RefineStages.ShotsMayBeRefilled(VideoVisualStyle.AiImagery).Should().BeFalse();
    }

    [Fact]
    public void The_two_free_visuals_operations_agree_on_which_videos_qualify()
    {
        // Pinning a picture and re-filling a moment are different operations
        // that happen to share a lane, and they are refused for the same
        // reason. If these ever disagree, one of them is wrong.
        foreach (var style in Enum.GetValues<VideoVisualStyle>())
        {
            RefineStages.ShotsMayBeRefilled(style)
                .Should().Be(RefineStages.PicturesMayBePinned(style), style.ToString());
        }
    }

    // ── the plan the worker will read ───────────────────────────────────

    [Fact]
    public void A_shot_edit_plan_re_enters_at_visuals_and_says_so()
    {
        var plan = RefinePlan.ForShotEdits(Instruction, Now);

        plan.Stage.Should().Be(RefineStage.Visuals);
        plan.EditsShots.Should().BeTrue(
            "the worker refuses this stage unless a flag distinguishes it");
    }

    [Fact]
    public void A_shot_edit_plan_is_not_a_picture_plan()
    {
        // Two flags, two operations. The worker branches on which, and setting
        // both would make it interpret a sentence *and* pin pictures that were
        // never attached.
        RefinePlan.ForShotEdits(Instruction, Now).PinsOwnPictures.Should().BeFalse();
        RefinePlan.ForPictures(1, Now).EditsShots.Should().BeFalse();
    }

    [Fact]
    public void The_pharmacists_own_words_are_kept_exactly()
    {
        // The confirm step compares this against what they send back, and they
        // are shown it again later when asking what happened to this video.
        var plan = RefinePlan.ForShotEdits("  " + Instruction + "  ", Now);

        plan.Instruction.Should().Be(Instruction);
        plan.Describes(Instruction).Should().BeTrue();
    }

    [Fact]
    public void The_summary_promises_only_what_this_lane_can_do()
    {
        // The fear here is losing narration that was already approved, so the
        // summary has to say what is kept, not only what changes.
        var summary = RefineStages.DescribeShotEdits();

        summary.Should().ContainAll("words", "voice", "costs nothing");
        summary.Should().NotContainAny("Pexels", "visuals", "stage", "Replicate");
    }

    [Fact]
    public void An_empty_instruction_cannot_become_a_shot_edit()
    {
        var make = () => RefinePlan.ForShotEdits("   ", Now);

        make.Should().Throw<DomainException>();
    }

    [Fact]
    public void An_over_long_instruction_is_refused_like_any_other()
    {
        var make = () => RefinePlan.ForShotEdits(new string('x', 5000), Now);

        make.Should().Throw<DomainException>();
    }

    // ── the closed lanes stay closed ────────────────────────────────────

    [Fact]
    public void Only_the_editor_is_still_free_to_reach_from_a_sentence()
    {
        // `Spends` and `IsEnabled` are untouched by this feature. Shot edits are
        // a separate door on top, not a widening of this one — if this starts
        // returning true for Visuals, "rewrite the narration" is one
        // classification away from running.
        foreach (var stage in Enum.GetValues<RefineStage>())
        {
            RefineStages.IsEnabled(stage).Should().Be(stage == RefineStage.Editor);
        }
    }

    [Fact]
    public void Writing_and_voicing_remain_out_of_reach()
    {
        // The two that would make a video shorter — which is the one thing this
        // feature tells the pharmacist it cannot do.
        RefineStages.IsEnabled(RefineStage.Scriptwriter).Should().BeFalse();
        RefineStages.IsEnabled(RefineStage.Voice).Should().BeFalse();
    }

    // ── the worker's copy of the rule ───────────────────────────────────

    [Fact]
    public void The_worker_admits_exactly_this_lane_and_no_other()
    {
        // The worker refuses any stage outside FREE_STAGES as the guard between
        // a routing mistake and an unbudgeted bill. This feature needs it to
        // make one more exception, and an exception written in another language
        // is the fault shape this codebase keeps hitting.
        var worker = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "apps", "ai-service", "app", "video_agent", "worker.py"));

        // Closed by a paren alone on its line, not by the first `)` in the
        // expression. The condition contains a bracketed `(a or b)`, and a lazy
        // match to `)` stopped there — capturing a fragment in which every
        // assertion below happened to look false, so the test went green while
        // checking nothing.
        var guard = Regex.Match(
            worker, @"free_for_pictures\s*=\s*\(([\s\S]{0,400}?)\n\s*\)\n");
        guard.Success.Should().BeTrue("worker.py should compute free_for_pictures");

        var condition = guard.Groups[1].Value;
        condition.Should().Contain("edits_shots", "the shot-edit flag must open it");
        condition.Should().Contain("pins_pictures", "the picture flag still opens it");
        condition.Should().Contain("\"visuals\"", "and only that one stage");
        condition.Should().Contain("ai_imagery", "an AI-imagery video must stay refused");
    }

    [Fact]
    public void The_worker_reads_the_same_field_name_this_side_writes()
    {
        // `editsShots` crosses as a Mongo field. A rename on either side is
        // silent: the flag simply reads as false and every shot edit is
        // refused, which looks like the feature was never built.
        var worker = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "apps", "ai-service", "app", "video_agent", "worker.py"));

        worker.Should().Contain("\"editsShots\"");

        var document = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "apps", "api", "src", "Acme.Infrastructure",
                "Persistence", "VideoDocument.cs"));

        document.Should().Contain("BsonElement(\"editsShots\")");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "apps", "ai-service")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repository root from the test output directory. "
            + "This test reads the worker's copy of the rule, so it needs the tree.");
    }
}
