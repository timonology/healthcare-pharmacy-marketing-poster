using System.Text.RegularExpressions;
using Acme.Application.Videos;
using Acme.Domain.Common;
using Acme.Domain.Videos;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Videos;

/// <summary>
/// Adding the pharmacy's own pictures to a video that is already made.
/// </summary>
/// <remarks>
/// <para>
/// <b>This opens a stage that is otherwise closed, so the boundary is the
/// test.</b> Everything except the editor is held shut because it spends, and
/// re-entering at <c>visuals</c> is how footage changes. What makes this
/// particular re-entry free is narrow and easy to widen by accident: no model
/// is asked anything, the script and narration come back from storage, the
/// stock footage is re-fetched from Pexels at no cost, and the pictures already
/// belong to the pharmacy.
/// </para>
/// <para>
/// Two things must therefore stay true, and neither is obvious from reading the
/// happy path: "find me different footage" — the same stage, reached from a
/// sentence — is still refused, and an AI-imagery video is refused too, because
/// re-cutting one regenerates every beat through Replicate.
/// </para>
/// </remarks>
public class VideoPictureRefineTests
{
    private static readonly DateTime Now = new(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);

    private static VideoImage Picture(string name = "a.jpg") =>
        VideoImage.Create($"videos/owner-1/attachments/{name}", VideoImageSource.Upload, null);

    /// <remarks>
    /// Forced through reflection rather than driven through a transition,
    /// because the domain has none: a render is finished by the Python worker
    /// writing to Mongo, so <c>Ready</c> is a state this side reads and never
    /// sets. The same approach <c>RefinePlanTests</c> takes, for the same
    /// reason.
    /// </remarks>
    private static Video Ready(
        VideoVisualStyle style = VideoVisualStyle.Realistic,
        IReadOnlyList<VideoImage>? images = null)
    {
        var v = Video.Create(
            "owner-1", "Flu clinic", "flu jabs",
            VideoScriptSource.Ai, style, null, null, null, null, images);
        Force(v, nameof(Video.Status), VideoStatus.Ready);
        return v;
    }

    private static void Force(object target, string name, object? value) =>
        target.GetType()
            .GetProperty(name, System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(target, value);

    // ── which videos may take pictures ──────────────────────────────────

    [Fact]
    public void A_stock_footage_video_may_have_pictures_pinned()
    {
        RefineStages.PicturesMayBePinned(VideoVisualStyle.Realistic).Should().BeTrue();
    }

    [Fact]
    public void An_ai_imagery_video_may_not()
    {
        // Re-entering visuals on one of these regenerates every beat through
        // Replicate at around five cents — which is exactly the bill the closed
        // stages exist to prevent. Free for a stock video, not for this one.
        RefineStages.PicturesMayBePinned(VideoVisualStyle.AiImagery).Should().BeFalse();

        RefineStages.WhyPicturesRefused(VideoVisualStyle.AiImagery)
            .Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_refusal_is_written_for_a_pharmacist_not_an_engineer()
    {
        var why = RefineStages.WhyPicturesRefused(VideoVisualStyle.AiImagery);

        why.Should().NotContainAny("Replicate", "visuals", "stage", "AiImagery");
        why.Should().Contain("Create a new video", "there has to be a way forward");
    }

    [Fact]
    public void Asking_why_a_permitted_video_was_refused_is_a_programming_error()
    {
        // Not an empty string. A caller reaching for a refusal that does not
        // exist has a bug, and handing back "" would put a blank message on
        // screen instead of failing where the mistake is.
        var ask = () => RefineStages.WhyPicturesRefused(VideoVisualStyle.Realistic);

        ask.Should().Throw<InvalidOperationException>();
    }

    // ── the closed lane stays closed ────────────────────────────────────

    [Fact]
    public void Visuals_is_still_closed_as_a_stage()
    {
        // <b>What this asserts changed meaning, so it is worth restating.</b>
        // When this was written, it meant "find me different footage" could not
        // be asked for at all. It can now — see `VideoShotEditTests` — through a
        // separate door that re-searches free stock and spends nothing.
        //
        // What it means today is narrower and still worth pinning: the *stage*
        // is closed, so opening it stays a deliberate act with its own name and
        // its own rules. If `IsEnabled` ever returns true here, every visuals
        // refine becomes reachable, including on an AI-imagery video where it
        // regenerates every beat through Replicate.
        RefineStages.IsEnabled(RefineStage.Visuals).Should().BeFalse();
        RefineStages.Spends(RefineStage.Visuals).Should().BeTrue();
    }

    [Fact]
    public void Only_the_editor_is_free_to_reach_from_a_sentence()
    {
        foreach (var stage in Enum.GetValues<RefineStage>())
        {
            RefineStages.IsEnabled(stage).Should().Be(
                stage == RefineStage.Editor,
                "adding pictures is a separate door and must not have opened this one");
        }
    }

    // ── the plan the worker will read ───────────────────────────────────

    [Fact]
    public void A_picture_plan_re_enters_at_visuals_and_says_so()
    {
        var plan = RefinePlan.ForPictures(2, Now);

        plan.Stage.Should().Be(RefineStage.Visuals);
        plan.PinsOwnPictures.Should().BeTrue(
            "the worker refuses this stage unless this flag distinguishes it");
    }

    [Fact]
    public void A_picture_plan_records_how_many_it_added_so_the_worker_can_undo_them()
    {
        // Appended before the re-cut runs. If the re-cut never happens they were
        // never shown, and left on the list they became "your own photo" at the
        // first and last moments of every later refine.
        var plan = RefinePlan.ForPictures(2, Now);

        plan.AddedPictures.Should().Be(2);
        plan.Confirm().AddedPictures.Should().Be(2, "confirming must not lose it");
        RefinePlan.Create("make the captions bigger", RefineStage.Editor, "x", Now)
            .AddedPictures.Should().Be(0);
    }

    [Fact]
    public void The_worker_reads_the_added_picture_count_under_the_name_it_is_stored()
    {
        var worker = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "apps", "ai-service", "app", "video_agent", "worker.py"));
        var document = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "apps", "api", "src", "Acme.Infrastructure",
                "Persistence", "VideoDocument.cs"));

        var mapper = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "apps", "api", "src", "Acme.Infrastructure",
                "Persistence", "VideoMapper.cs"));

        document.Should().Contain("[BsonElement(\"addedPictures\")]");
        worker.Should().Contain("\"addedPictures\"");
        mapper.Should().Contain("AddedPictures = plan.AddedPictures",
            "written to storage, or the worker never learns the count");
        mapper.Should().Contain("AddedPictures = d.AddedPictures",
            "read back, or re-saving the video drops it");
    }

    [Fact]
    public void A_picture_plan_carries_a_sentence_for_the_pharmacist()
    {
        // `Instruction` is what they are shown when they ask what happened to
        // this video. Empty would be a worse answer than a sentence.
        var plan = RefinePlan.ForPictures(1, Now);

        plan.Instruction.Should().NotBeNullOrWhiteSpace();
        plan.Summary.Should().Contain("kept", "the fear here is losing the narration");
    }

    [Fact]
    public void A_plan_for_no_pictures_is_refused()
    {
        var make = () => RefinePlan.ForPictures(0, Now);

        make.Should().Throw<DomainException>();
    }

    // ── appending, and the cap ──────────────────────────────────────────

    [Fact]
    public void Pictures_are_added_to_the_ones_already_there()
    {
        // "Add an image to the video as well" — the ones chosen at create were
        // chosen deliberately, and dropping them is not what was asked for.
        var video = Ready(images: new[] { Picture("first.jpg") });

        video.AddPictures(new[] { Picture("second.jpg") }, Now);

        video.Images.Should().HaveCount(2);
        video.Images[0].BlobKey.Should().EndWith("first.jpg", "the original is kept");
    }

    [Fact]
    public void The_cap_counts_what_is_there_plus_what_is_arriving()
    {
        // The reading that matters: checking the new ones alone would let four
        // be added to four.
        var video = Ready(images: new[] { Picture("1.jpg"), Picture("2.jpg"), Picture("3.jpg") });

        var addTwo = () => video.AddPictures(new[] { Picture("4.jpg"), Picture("5.jpg") }, Now);

        addTwo.Should().Throw<DomainException>()
            .WithMessage("*room for 1 more*", "it should say how much room is left");
    }

    [Fact]
    public void Filling_the_last_slot_exactly_is_allowed()
    {
        var video = Ready(images: new[] { Picture("1.jpg"), Picture("2.jpg"), Picture("3.jpg") });

        video.AddPictures(new[] { Picture("4.jpg") }, Now);

        video.Images.Should().HaveCount(Video.VideoImageLimit);
    }

    [Fact]
    public void A_full_video_says_it_is_full_rather_than_counting()
    {
        var video = Ready(images: Enumerable.Range(1, 4).Select(n => Picture($"{n}.jpg")).ToArray());

        var addMore = () => video.AddPictures(new[] { Picture("5.jpg") }, Now);

        addMore.Should().Throw<DomainException>().WithMessage("*already has 4*");
    }

    [Fact]
    public void Adding_nothing_is_refused()
    {
        var video = Ready();

        var addNone = () => video.AddPictures(Array.Empty<VideoImage>(), Now);

        addNone.Should().Throw<DomainException>();
    }

    // ── the state it leaves the video in ────────────────────────────────

    [Fact]
    public void Adding_pictures_queues_the_recut()
    {
        var video = Ready();

        video.AddPictures(new[] { Picture() }, Now);

        video.Status.Should().Be(VideoStatus.Pending);
        video.Refine!.PinsOwnPictures.Should().BeTrue();
        video.Refine!.Confirmed.Should().BeTrue(
            "there is no separate confirm step — the file is the instruction");
    }

    [Fact]
    public void A_video_that_is_not_finished_keeps_the_pictures_out()
    {
        // The ordering that matters: if the pictures were appended before the
        // status was checked, a refused re-cut would leave them attached and the
        // next refine would pin them without anybody asking.
        var video = Video.Create(
            "owner-1", "Flu clinic", "flu jabs",
            VideoScriptSource.Ai, VideoVisualStyle.Realistic, null, null, null, null, null);

        var add = () => video.AddPictures(new[] { Picture() }, Now);

        add.Should().Throw<DomainException>();
        video.Images.Should().BeEmpty("nothing should have been kept");
    }

    // ── the two sides of the lane agree ─────────────────────────────────

    [Fact]
    public void The_worker_allows_exactly_the_lane_this_side_opens()
    {
        // The Python worker refuses any stage outside FREE_STAGES — deliberately,
        // as the guard between a routing mistake and an unbudgeted bill. This
        // feature needs it to make one exception, and an exception written in
        // another language is the fault shape this codebase keeps hitting.
        var worker = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "apps", "ai-service", "app", "video_agent", "worker.py"));

        var guard = Regex.Match(
            worker, // Closed by a paren alone on its line, not by the first `)` in the
            // expression — the condition contains a bracketed `(a or b)`, and a
            // lazy match to `)` stopped there, silently capturing a fragment in
            // which every assertion below happened to look false.
            @"free_for_pictures\s*=\s*\(([\s\S]{0,400}?)\n\s*\)\n");

        guard.Success.Should().BeTrue("worker.py should compute free_for_pictures");

        var condition = guard.Groups[1].Value;
        condition.Should().Contain("pins_pictures",
            "it must be gated on the flag this side sets");
        condition.Should().Contain("\"visuals\"",
            "it must admit that one stage only");
        condition.Should().Contain("ai_imagery",
            "an AI-imagery video costs Replicate to re-cut and must stay refused");
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
