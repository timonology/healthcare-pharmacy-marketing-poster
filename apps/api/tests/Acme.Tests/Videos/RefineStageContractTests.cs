using Acme.Domain.Videos;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Videos;

/// <summary>
/// The stage list exists in two languages, and the two must agree.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RefineStage"/> is the authority: it decides what a stage costs
/// and whether a pharmacist may reach it. The Python side keeps its own copy in
/// <c>app/video_agent/stages.py</c>, because the classifier has to name the
/// stages in a prompt and the worker has to map one to a graph entry node.
/// </para>
/// <para>
/// That is a seam, and seams are where this codebase's faults live — two token
/// lists that had to agree by hand, two profile write paths that had to agree
/// about silence, two processes writing one document. This one is worse than
/// most, because the thing the two lists disagree about is <b>which stages
/// spend money</b>.
/// </para>
/// <para>
/// So this test reads the Python source. It cannot make the two agree, but it
/// can stop them diverging quietly.
/// </para>
/// </remarks>
public class RefineStageContractTests
{
    private static readonly string StagesSource = ReadStages();

    [Fact]
    public void The_python_stage_list_matches_the_enum_in_name_and_order()
    {
        // Order matters as much as membership. Both sides treat "earlier in the
        // list" as "redoes more", so a list that agrees on names but not on
        // order would route a caption fix through the scriptwriter.
        var expected = Enum.GetValues<RefineStage>()
            .OrderBy(s => (int)s)
            .Select(s => s.ToString().ToLowerInvariant())
            .ToList();

        var actual = ParseTuple("STAGES");

        actual.Should().Equal(expected,
            "app/video_agent/stages.py declares STAGES = {0}, and RefineStage "
            + "is {1}. The two describe the same pipeline and the Python copy "
            + "exists only because the classifier needs the names in a prompt",
            string.Join(", ", actual), string.Join(", ", expected));
    }

    [Fact]
    public void The_two_sides_agree_on_which_stages_are_free()
    {
        var expected = Enum.GetValues<RefineStage>()
            .Where(s => !RefineStages.Spends(s))
            .Select(s => s.ToString().ToLowerInvariant())
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var actual = ParseSet("FREE_STAGES").OrderBy(s => s, StringComparer.Ordinal).ToList();

        actual.Should().Equal(expected,
            "FREE_STAGES in stages.py is {0} and RefineStages.Spends "
            + "says {1} is free. A stage the Python side believes is free and "
            + "the .NET side believes is paid is a bill nobody approved",
            string.Join(", ", actual), string.Join(", ", expected));
    }

    [Fact]
    public void Both_sides_refuse_the_same_length_of_instruction()
    {
        // These were 1000, 1000 and 2000 — the FastAPI route accepted twice
        // what the classifier behind it would try to route, so the long case
        // came back as an unexplained shrug instead of "one change at a time".
        var match = System.Text.RegularExpressions.Regex.Match(
            StagesSource, @"MAX_INSTRUCTION_CHARS\s*=\s*(\d+)");

        match.Success.Should().BeTrue(
            "stages.py no longer declares MAX_INSTRUCTION_CHARS");

        int.Parse(match.Groups[1].Value).Should().Be(RefinePlan.MaxInstructionLength,
            "one side accepting what the other will not act on means the "
            + "pharmacist is refused without being told why");
    }

    [Fact]
    public void The_browser_stops_typing_at_the_same_length_the_api_accepts()
    {
        // A third copy, because a textarea needs a literal. The failure it
        // guards against is quieter than the Python one: a maxLength above the
        // real limit lets somebody write a long instruction, press the button
        // and be refused by validation they were given no warning of; below it,
        // the box stops them mid-sentence for no reason they can see.
        var match = System.Text.RegularExpressions.Regex.Match(
            ReadSharedVideoTypes(), @"MAX_REFINE_INSTRUCTION_CHARS\s*=\s*(\d+)");

        match.Success.Should().BeTrue(
            "packages/shared-types/src/video.ts no longer exports "
            + "MAX_REFINE_INSTRUCTION_CHARS, which the refine dialog's textarea "
            + "uses as its maxLength");

        int.Parse(match.Groups[1].Value).Should().Be(RefinePlan.MaxInstructionLength,
            "the browser would let a pharmacist write an instruction the API "
            + "then refuses, or cut one short that it would have accepted");
    }

    /// <summary>Walks up from the test binary to the shared TypeScript types.</summary>
    private static string ReadSharedVideoTypes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "packages", "shared-types", "src", "video.ts");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find packages/shared-types/src/video.ts from the test "
            + "output directory. This test reads the browser's copy of the "
            + "instruction limit, so it needs the repository tree.");
    }

    [Fact]
    public void Neither_side_reclaims_a_render_before_a_lease_would_have_lapsed()
    {
        // A `Rendering` row with no lease is judged by silence: both sides wait
        // for it to go quiet before presuming it abandoned. The wait must be
        // longer than the lease itself — shorter, and a live render is declared
        // stalled by the API and reclaimed by a second worker, which is the
        // fault this replaced and which cost a pharmacy two renders.
        //
        // An inequality, not equality. The two are allowed to differ; only one
        // direction is dangerous.
        var worker = ReadWorker();

        var lease = Seconds(worker, "LEASE_SECONDS");
        var grace = Seconds(worker, "UNLEASED_GRACE_SECONDS");

        grace.Should().BeGreaterThan(lease,
            "the worker gives an unleased row {0}s before reclaiming it, and a "
            + "lease lasts {1}s. Reclaiming sooner than a lease lasts takes "
            + "renders that are still running", grace, lease);

        Video.UnleasedRenderGrace.Should().BeGreaterThan(TimeSpan.FromSeconds(lease),
            "the API calls an unleased row stranded after {0}s while the worker's "
            + "lease lasts {1}s. Shorter, and a pharmacist is shown "
            + "\"Stalled — try again\" under a moving progress bar",
            Video.UnleasedRenderGrace.TotalSeconds, lease);
    }

    private static int Seconds(string source, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            source, $@"^{name}\s*=\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        match.Success.Should().BeTrue(
            "worker.py no longer declares {0} at the top level; this test reads "
            + "it to keep the two sides' timings in step", name);

        return int.Parse(match.Groups[1].Value);
    }

    /// <summary>Walks up from the test binary to the render worker's source.</summary>
    private static string ReadWorker()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "apps", "ai-service", "app", "video_agent", "worker.py");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find apps/ai-service/app/video_agent/worker.py from the "
            + "test output directory. This test reads the worker's lease timings "
            + "to check them against the API's, so it needs the repository tree.");
    }

    [Fact]
    public void Every_stage_has_a_description_a_pharmacist_could_act_on()
    {
        // The description is what somebody reads before agreeing to spend a
        // credit. A stage added to the enum without one falls to the "Unknown
        // change." default, and "Unknown change. Proceed?" is not a question
        // anybody can answer.
        foreach (var stage in Enum.GetValues<RefineStage>())
        {
            var text = RefineStages.Describe(stage);

            text.Should().NotBe("Unknown change.",
                "{0} has no description, so the confirmation prompt would ask "
                + "the pharmacist to approve something unnamed", stage);
            text.Should().NotContain(stage.ToString(),
                "{0}'s description names the pipeline stage. A pharmacist does "
                + "not know what a scriptwriter node is; they know what will "
                + "change about their video", stage);
        }
    }

    [Fact]
    public void Only_the_editor_is_open_while_spend_is_unmeasured()
    {
        // Pinned deliberately, so opening a paid lane is a visible, arguable
        // change rather than something that drifts in. The decision record
        // requires measured per-video spend first, and ElevenLabs is not
        // metered at all — see DECISION-video-brand-and-refine.md.
        foreach (var stage in Enum.GetValues<RefineStage>())
        {
            RefineStages.IsEnabled(stage).Should().Be(stage == RefineStage.Editor,
                "{0} should be {1}. Changing this is a pricing decision, not a "
                + "code change — if the meters now exist, update the decision "
                + "record in the same commit", stage,
                stage == RefineStage.Editor ? "open" : "closed");
        }
    }

    [Fact]
    public void An_unrecognised_stage_name_is_refused_rather_than_defaulted()
    {
        foreach (var junk in new[] { null, "", "  ", "uploader", "EDITOR ", "42", "-1" })
        {
            var parsed = RefineStages.Parse(junk);

            if (junk?.Trim().Equals("editor", StringComparison.OrdinalIgnoreCase) == true)
            {
                parsed.Should().Be(RefineStage.Editor, "whitespace is not a typo");
                continue;
            }

            parsed.Should().BeNull(
                "'{0}' is not a stage. Defaulting it to the cheapest stage would "
                + "run an editor pass for a request the editor cannot satisfy; "
                + "defaulting it to the earliest would spend money on a guess",
                junk ?? "<null>");
        }
    }

    /// <summary>Reads a `NAME: ... = (...)` tuple of quoted strings.</summary>
    private static List<string> ParseTuple(string name) => ParseQuoted(name, '(', ')');

    /// <summary>Reads a `NAME: ... = frozenset({...})` of quoted strings.</summary>
    private static List<string> ParseSet(string name) => ParseQuoted(name, '{', '}');

    private static List<string> ParseQuoted(string name, char open, char close)
    {
        // Anchored to the start of a line, because "STAGES" is a suffix of
        // "FREE_STAGES". A plain substring search finds the right declaration
        // today only because STAGES happens to be written first — reorder the
        // file and this test would quietly parse the wrong collection, which is
        // a poor quality in a test whose entire job is catching drift.
        var declaration = System.Text.RegularExpressions.Regex.Match(
            StagesSource,
            $@"^{System.Text.RegularExpressions.Regex.Escape(name)}\s*[:=]",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        declaration.Success.Should().BeTrue(
            "stages.py no longer declares {0} at the top level. This test reads "
            + "it to keep the two sides of the pipeline in step and cannot do so",
            name);

        var at = declaration.Index;

        var start = StagesSource.IndexOf(open, at);
        var end = StagesSource.IndexOf(close, start);
        start.Should().BeGreaterThanOrEqualTo(0);
        end.Should().BeGreaterThan(start);

        return System.Text.RegularExpressions.Regex
            .Matches(StagesSource[start..end], "\"([a-z_]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    /// <summary>Walks up from the test binary to the Python source.</summary>
    private static string ReadStages()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "apps", "ai-service", "app", "video_agent", "stages.py");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find apps/ai-service/app/video_agent/stages.py from the "
            + "test output directory. This test reads the Python stage list to "
            + "check it against RefineStage, so it needs the repository tree.");
    }
}
