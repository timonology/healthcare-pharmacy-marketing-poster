using Acme.Application.Videos;
using Acme.Domain.Common;
using Acme.Domain.Videos;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Videos;

/// <summary>
/// The shot picker: the pharmacist taps the shot to change, and nothing about
/// what the shot is said to show can overrule that.
/// </summary>
/// <remarks>
/// Replaced a model that chose the shot from the pharmacist's sentence by
/// reading stock-library titles. It refused "remove the last shot which shows
/// medicine" because the title said "employees on a couch". The pharmacist had
/// watched the video.
/// </remarks>
public class VideoShotPickerTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);

    private const string FourMoments = """
        {"segments":[
          {"text":"Carrying extra weight strains the heart.","start":0,"end":3},
          {"text":"Small changes add up.","start":3,"end":6},
          {"text":"Ask us about a weight check.","start":6,"end":9},
          {"text":"Book today at New Life.","start":9,"end":12}]}
        """;

    private static VideoShotSource Source(
        string? metadata = FourMoments,
        IReadOnlyList<ShotTiming>? timeline = null,
        IReadOnlyList<ShotClip>? clips = null) =>
        new("owner-1", metadata, timeline ?? Array.Empty<ShotTiming>(), clips ?? Array.Empty<ShotClip>());

    // ── the list the pharmacist picks from ──────────────────────────────

    [Fact]
    public void Every_moment_is_a_tile_with_its_caption()
    {
        var shots = VideoShots.Build(Source());

        shots.Select(s => s.Index).Should().Equal(0, 1, 2, 3);
        shots[3].Caption.Should().Be("Book today at New Life.");
    }

    [Fact]
    public void Times_come_from_the_render_when_it_recorded_them()
    {
        // The rendered timeline is where the shots really were; the script's
        // plan is only an estimate, and was 5 seconds short on the reported video.
        var timeline = new[]
        {
            new ShotTiming(0, 0, 4.95, "videos/owner-1/v1/shots/00.jpg"),
            new ShotTiming(1, 4.95, 9.9, null),
            new ShotTiming(2, 9.9, 14.85, "videos/owner-1/v1/shots/02.jpg"),
            new ShotTiming(3, 14.85, 19.8, "videos/owner-1/v1/shots/03.jpg"),
        };

        var shots = VideoShots.Build(Source(timeline: timeline));

        shots[3].StartSeconds.Should().Be(14.85);
        shots[3].EndSeconds.Should().Be(19.8);
        shots[1].HasThumbnail.Should().BeFalse("no still was kept, so the tile is numbered");
        shots[3].HasThumbnail.Should().BeTrue();
    }

    [Fact]
    public void Filler_shots_that_belong_to_no_moment_are_not_tiles()
    {
        var timeline = new[] { new ShotTiming(0, 0, 3, null), new ShotTiming(null, 3, 6, null) };

        VideoShots.Build(Source(timeline: timeline)).Select(s => s.Index).Should().Equal(0);
    }

    [Fact]
    public void The_pharmacys_own_photo_is_labelled()
    {
        var clips = new[] { new ShotClip(0, "user", null), new ShotClip(1, "aligned", "a salad") };

        var shots = VideoShots.Build(Source(clips: clips));

        shots[0].IsPhoto.Should().BeTrue();
        shots[1].IsPhoto.Should().BeFalse();
        shots[1].Shows.Should().Be("a salad", "alt text only — never used to choose a shot");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"segments\": \"nope\"}")]
    public void Unreadable_metadata_gives_no_tiles_rather_than_an_error(string? metadata)
    {
        VideoShots.Build(Source(metadata)).Should().BeEmpty();
    }

    // ── stills ──────────────────────────────────────────────────────────

    [Fact]
    public void A_still_is_served_only_from_this_videos_own_shots_folder()
    {
        var timeline = new[]
        {
            new ShotTiming(0, 0, 3, "videos/owner-1/v1/shots/00.jpg"),
            new ShotTiming(1, 3, 6, "videos/owner-2/v9/shots/00.jpg"),
            new ShotTiming(2, 6, 9, "videos/owner-1/v1/shots/../../other.jpg"),
        };
        var source = Source(timeline: timeline);

        VideoShots.ThumbnailKey(source, "v1", 0).Should().Be("videos/owner-1/v1/shots/00.jpg");
        VideoShots.ThumbnailKey(source, "v1", 1).Should().BeNull("another pharmacy's file");
        VideoShots.ThumbnailKey(source, "v1", 2).Should().BeNull("a path that climbs out");
        VideoShots.ThumbnailKey(source, "v1", 3).Should().BeNull("no such shot");
    }

    // ── choosing ────────────────────────────────────────────────────────

    [Fact]
    public void A_tapped_shot_is_taken_as_given()
    {
        var choice = ShotChoice.Create(3, "  a person on a scale  ", shotCount: 4);

        var plan = RefinePlan.ForChosenShots(new[] { choice }, Now);

        plan.ChosenShots.Should().ContainSingle()
            .Which.Should().Match<ShotChoice>(c => c.Index == 3 && c.Show == "a person on a scale");
        plan.EditsShots.Should().BeTrue("the free shot-edit lane");
        plan.Stage.Should().Be(RefineStage.Visuals);
        plan.Instruction.Should().Contain("shot 4");
    }

    [Fact]
    public void The_last_tile_is_accepted_when_an_earlier_moment_was_skipped()
    {
        // Moment 2's clip would not open, so the editor skipped it: the tiles
        // are moments 0, 1 and 3. Checking "index < count of tiles" refused the
        // last tile — the one pharmacists tap most.
        var timeline = new[]
        {
            new ShotTiming(0, 0, 5, null), new ShotTiming(1, 5, 10, null),
            new ShotTiming(3, 10, 19.8, null),
        };
        var tiles = VideoShots.Build(Source(timeline: timeline)).Select(s => s.Index).ToHashSet();

        var choice = () => ShotChoice.Create(3, "a scale", tiles);
        var gap = () => ShotChoice.Create(2, "a scale", tiles);

        choice.Should().NotThrow();
        gap.Should().Throw<DomainException>("moment 2 is not on screen");
    }

    [Fact]
    public void A_shot_with_nothing_said_is_allowed()
    {
        // The worker searches the video's own subject.
        ShotChoice.Create(0, null, shotCount: 4).Show.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void A_shot_the_video_does_not_have_is_refused(int index)
    {
        var act = () => ShotChoice.Create(index, "x", shotCount: 4);

        act.Should().Throw<DomainException>().WithMessage("*no longer in this video*");
    }

    [Fact]
    public void An_essay_is_not_a_stock_search()
    {
        var act = () => ShotChoice.Create(0, new string('x', ShotChoice.MaxShowLength + 1), 4);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Nothing_tapped_too_many_or_the_same_twice_is_refused()
    {
        var none = () => RefinePlan.ForChosenShots(Array.Empty<ShotChoice>(), Now);
        var many = () => RefinePlan.ForChosenShots(
            Enumerable.Range(0, ShotChoice.MaxPerRequest + 1)
                .Select(i => ShotChoice.Create(i, "x", 12)).ToList(), Now);
        var twice = () => RefinePlan.ForChosenShots(
            new[] { ShotChoice.Create(1, "a", 4), ShotChoice.Create(1, "b", 4) }, Now);

        none.Should().Throw<DomainException>();
        many.Should().Throw<DomainException>();
        twice.Should().Throw<DomainException>();
    }

    [Fact]
    public void Confirming_keeps_the_tapped_shots()
    {
        var plan = RefinePlan.ForChosenShots(new[] { ShotChoice.Create(2, "salad", 4) }, Now);

        plan.Confirm().ChosenShots.Should().ContainSingle().Which.Index.Should().Be(2);
    }

    // ── the worker reads what .NET writes ───────────────────────────────

    [Fact]
    public void The_worker_reads_tapped_shots_under_the_names_they_are_stored()
    {
        var root = FindRepoRoot();
        var document = File.ReadAllText(Path.Combine(root, "apps", "api", "src",
            "Acme.Infrastructure", "Persistence", "VideoDocument.cs"));
        var mapper = File.ReadAllText(Path.Combine(root, "apps", "api", "src",
            "Acme.Infrastructure", "Persistence", "VideoMapper.cs"));
        var worker = File.ReadAllText(Path.Combine(root, "apps", "ai-service", "app",
            "video_agent", "worker.py"));
        var shotEdits = File.ReadAllText(Path.Combine(root, "apps", "ai-service", "app",
            "video_agent", "shot_edits.py"));

        document.Should().Contain("[BsonElement(\"shotEdits\")]");
        document.Should().Contain("[BsonElement(\"index\")]");
        document.Should().Contain("[BsonElement(\"show\")]");
        mapper.Should().Contain("ShotEdits = plan.ChosenShots.Count == 0",
            "written to storage, or the worker never sees the tapped shots");
        mapper.Should().Contain("ShotChoice.Restore(e.Index, e.Show)", "read back");
        worker.Should().Contain("refine_doc.get(\"shotEdits\")");
        shotEdits.Should().Contain("item.get(\"index\")").And.Contain("item.get(\"show\")");
    }

    [Fact]
    public void The_shot_cap_agrees_with_the_worker()
    {
        var shotEdits = File.ReadAllText(Path.Combine(FindRepoRoot(), "apps", "ai-service",
            "app", "video_agent", "shot_edits.py"));

        shotEdits.Should().Contain($"MAX_EDITS = {ShotChoice.MaxPerRequest}");
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
            "Could not find the repository root. This test reads the worker's code.");
    }
}
