using System.Reflection;
using Acme.Domain.Common;
using Acme.Domain.Videos;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Videos;

/// <summary>
/// What happens to a render that dies, and who pays for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A render used to be strandable, and the pharmacy was already charged.</b>
/// The worker claimed a job by flipping <c>Pending</c> to <c>Rendering</c> with
/// nothing recording who held it or for how long, and nothing anywhere read
/// <c>Rendering</c> back — no lease, no expiry, no sweeper. Its claim filter
/// matched <c>Pending</c> only, so a job abandoned mid-flight was never picked
/// up again, and <c>Retry</c> refused anything that was not <c>Failed</c>,
/// which such a job never becomes.
/// </para>
/// <para>
/// The quota is spent in <c>VideoService.CreateAsync</c>, not on success. So a
/// container restart, a deploy, or ffmpeg taking an OOM kill cost a monthly
/// allowance slot or a purchased credit and left a row that could neither finish
/// nor be retried. None of the plausible failures produce <c>Failed</c> —
/// reaching it requires the worker that died to still be alive to write it.
/// </para>
/// </remarks>
public class VideoRenderRecoveryTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    private static Video Queued() => Video.Create(
        ownerId: "owner-1",
        name: "Flu jab explainer",
        topic: "why book your flu jab this autumn",
        scriptSource: VideoScriptSource.Ai,
        visualStyle: VideoVisualStyle.Realistic,
        description: null,
        customScript: null,
        voiceId: null,
        styleTone: null);

    /// <summary>
    /// Puts the entity into a state only the Python worker can produce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Failed</c> and <c>Ready</c> have no transition method, because the
    /// .NET domain never performs them: the render worker writes those fields
    /// straight into Mongo and this side only ever meets them coming back
    /// through <c>VideoMapper.Hydrate</c>, which sets them by reflection for the
    /// same reason. So a test that wants to ask "what does Retry do to a row the
    /// worker left in this state" has to reconstitute the row the way the mapper
    /// does.
    /// </para>
    /// <para>
    /// It is also the only way to build the legacy shape that matters most —
    /// <c>Rendering</c> with no lease at all — because <see cref="Video.Claim"/>
    /// always writes one. Every row claimed before leases existed looks like
    /// that, and those are precisely the ones stuck today.
    /// </para>
    /// </remarks>
    private static Video AsWorkerLeftIt(
        Video video,
        VideoStatus status,
        string? leaseOwner = null,
        DateTime? leaseExpiresAtUtc = null,
        string? blobKey = null,
        string? thumbnailBlobKey = null,
        string? note = null)
    {
        Set(video, nameof(Video.Status), status);
        Set(video, nameof(Video.LeaseOwner), leaseOwner);
        Set(video, nameof(Video.LeaseExpiresAtUtc), leaseExpiresAtUtc);
        if (blobKey is not null) Set(video, nameof(Video.BlobKey), blobKey);
        if (thumbnailBlobKey is not null) Set(video, nameof(Video.ThumbnailBlobKey), thumbnailBlobKey);
        if (note is not null) Set(video, nameof(Video.Note), note);
        return video;

        static void Set(object target, string name, object? value) =>
            target.GetType()
                .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(target, value);
    }

    /* ── the case that was unrecoverable ─────────────────────────────── */

    [Fact]
    public void A_render_whose_worker_died_can_be_retried()
    {
        var video = Queued();
        video.Claim("renderer-a", Now.AddMinutes(10));

        // Eleven minutes later the lease has lapsed and nobody renewed it,
        // because the process holding it is gone.
        var later = Now.AddMinutes(11);

        video.IsStranded(later).Should().BeTrue();

        var retry = () => video.Retry(later);
        retry.Should().NotThrow(
            "the pharmacy paid for this render when they created it. Refusing the "
            + "retry means they paid for a video that can never be produced");

        video.Status.Should().Be(VideoStatus.Pending, "it goes back on the queue");
        video.LeaseOwner.Should().BeNull();
        video.LeaseExpiresAtUtc.Should().BeNull();
    }

    [Fact]
    public void A_render_claimed_before_leases_existed_can_be_retried_once_it_goes_quiet()
    {
        // Rendering with no lease at all. A missing lease still has to read as
        // lapsed eventually, or every row stranded by the old worker stays
        // stranded after the fix ships — but only once the row has stopped
        // being touched. See the next test for why the wait matters.
        var video = AsWorkerLeftIt(Queued(), VideoStatus.Rendering, leaseExpiresAtUtc: null);
        Touched(video, Now.Subtract(Video.UnleasedRenderGrace).AddMinutes(-1));

        video.IsStranded(Now).Should().BeTrue();
        video.Invoking(v => v.Retry(Now)).Should().NotThrow();
        video.Status.Should().Be(VideoStatus.Pending);
    }

    [Fact]
    public void A_render_with_no_lease_that_is_still_reporting_is_not_stranded()
    {
        // The bug this replaced, reported from the app: a video rendering
        // happily showed "Stalled — try again" under a progress bar that was
        // still moving.
        //
        // The cause was a worker older than the lease work. It wrote progress
        // notes but no lease, and the old rule called *any* lease-less
        // Rendering row stranded on sight. Since the worker's progress callback
        // writes the note, the lease and UpdatedAtUtc in one update, a row being
        // touched is a row someone is working on — whatever its lease says.
        var video = AsWorkerLeftIt(Queued(), VideoStatus.Rendering, leaseExpiresAtUtc: null);
        Touched(video, Now.AddSeconds(-2));

        video.IsStranded(Now).Should().BeFalse(
            "something wrote to this row two seconds ago, so a worker is alive "
            + "and holding it — offering Retry here invites a pharmacist to "
            + "start a second render of a video that is already being made");
    }

    [Fact]
    public void The_grace_is_longer_than_a_lease_would_have_lasted()
    {
        // The only dangerous direction. Shorter than the worker's lease and
        // live renders get declared stalled again — which is the whole fault.
        // The Python side's LEASE_SECONDS is 600; RefineStageContractTests pins
        // the inequality against the real file rather than this constant.
        Video.UnleasedRenderGrace.Should().BeGreaterThan(TimeSpan.FromSeconds(600));
    }

    /// <summary>Sets when the row was last written to.</summary>
    private static void Touched(Video video, DateTime whenUtc) =>
        typeof(Video)
            .GetProperty(nameof(Video.UpdatedAtUtc), BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(video, whenUtc);

    [Fact]
    public void A_render_that_is_genuinely_running_is_left_alone()
    {
        var video = Queued();
        video.Claim("renderer-a", Now.AddMinutes(10));

        video.IsStranded(Now.AddMinutes(1)).Should().BeFalse();

        var retry = () => video.Retry(Now.AddMinutes(1));
        retry.Should().Throw<DomainException>()
            .WithMessage("*still rendering*",
                "a second render of the same video pays for the imagery twice and "
                + "produces two files where the user asked for one");
    }

    [Fact]
    public void Progress_extends_the_claim()
    {
        // Why the lease is renewed rather than set once: research, a language
        // model, speech synthesis, downloads and an ffmpeg encode are each slow
        // enough that a fixed lease is either too short for a healthy job or too
        // long for a dead one.
        var video = Queued();
        video.Claim("renderer-a", Now.AddMinutes(10));

        video.RenewLease(Now.AddMinutes(20));

        video.IsStranded(Now.AddMinutes(15)).Should().BeFalse();
        video.LeaseOwner.Should().Be("renderer-a", "renewal does not change hands");
    }

    /* ── the states that were already right ──────────────────────────── */

    [Fact]
    public void A_failed_render_can_still_be_retried()
    {
        var video = AsWorkerLeftIt(
            Queued(), VideoStatus.Failed, note: "Pexels returned nothing for that topic");

        video.Invoking(v => v.Retry(Now)).Should().NotThrow();
        video.Status.Should().Be(VideoStatus.Pending);
        video.Note.Should().BeNull("the previous failure is not still shown as current");
    }

    [Fact]
    public void A_finished_video_cannot_be_retried()
    {
        var video = AsWorkerLeftIt(
            Queued(), VideoStatus.Ready, blobKey: "videos/owner-1/v.mp4");

        video.Invoking(v => v.Retry(Now))
            .Should().Throw<DomainException>().WithMessage("*already rendered*");
    }

    /* ── the part that must never change ─────────────────────────────── */

    [Fact]
    public void A_retry_is_the_same_video_not_a_new_one()
    {
        // <b>This is the no-double-charge property, pinned.</b>
        //
        // The monthly allowance and any purchased credit are spent by
        // EnforceVideoLimit in VideoService.CreateAsync — on creation, not on
        // success. A retry that produced a new row, or that grew a call to the
        // quota code, would charge a pharmacy twice for one video they never
        // received. Retry mutates in place and touches nothing to do with
        // billing, and that is the whole of the guarantee.
        var video = Queued();
        var id = video.Id;
        var owner = video.OwnerId;

        video.Claim("renderer-a", Now.AddMinutes(10));
        video.Retry(Now.AddMinutes(11));

        video.Id.Should().Be(id, "a retry re-queues the row it was given");
        video.OwnerId.Should().Be(owner);
    }

    [Fact]
    public void A_retry_keeps_whatever_the_last_attempt_managed_to_upload()
    {
        // If the previous attempt uploaded before failing, those bytes stay
        // reachable while the retry is in flight, rather than a video the user
        // could already play disappearing from under them.
        var video = AsWorkerLeftIt(
            Queued(), VideoStatus.Failed,
            blobKey: "videos/owner-1/v.mp4",
            thumbnailBlobKey: "videos/owner-1/v.jpg",
            note: "thumbnail upload failed");

        video.Retry(Now);

        video.BlobKey.Should().Be("videos/owner-1/v.mp4");
        video.ThumbnailBlobKey.Should().Be("videos/owner-1/v.jpg");
    }
}
