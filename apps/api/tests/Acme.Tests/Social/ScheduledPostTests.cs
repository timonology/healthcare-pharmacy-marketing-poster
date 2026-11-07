using Acme.Domain.Common;
using Acme.Domain.Social;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// Publish-request rules.
/// </summary>
/// <remarks>
/// The centre of gravity here is <see cref="ScheduledPost.PrepareRetry"/>. A
/// retry that re-attempts an already-published target posts the same poster to
/// a pharmacy's followers twice, and there is no way to retract that — so it
/// gets more tests than anything else in this file.
/// </remarks>
public class ScheduledPostTests
{
    private const string Owner = "owner-1";

    private static ScheduledPost Post(
        DateTime? scheduledFor = null,
        params (string, SocialPlatform)[] targets)
    {
        var list = targets.Length == 0
            ? new[] { ("conn-fb", SocialPlatform.FacebookPage) }
            : targets;

        return ScheduledPost.Create(
            Owner, PublishSourceKind.Poster, "poster-1", "Flu clinic",
            "Book your flu jab", scheduledFor, list);
    }

    /* ---------------- creation ---------------- */

    [Fact]
    public void Requires_at_least_one_target()
    {
        var ex = Assert.Throws<DomainException>(() =>
            ScheduledPost.Create(
                Owner, PublishSourceKind.Poster, "p", "n", "c", null,
                Array.Empty<(string, SocialPlatform)>()));

        Assert.Contains("at least one", ex.Message);
    }

    [Fact]
    public void Rejects_a_time_in_the_past()
    {
        Assert.Throws<DomainException>(() => Post(DateTime.UtcNow.AddHours(-1)));
    }

    [Fact]
    public void Rejects_a_time_more_than_a_year_ahead()
    {
        Assert.Throws<DomainException>(() => Post(DateTime.UtcNow.AddYears(2)));
    }

    [Fact]
    public void Rejects_an_over_long_caption()
    {
        var ex = Assert.Throws<DomainException>(() =>
            ScheduledPost.Create(
                Owner, PublishSourceKind.Poster, "p", "n", new string('x', 2201), null,
                new[] { ("c", SocialPlatform.FacebookPage) }));

        Assert.Contains("2200", ex.Message);
    }

    [Fact]
    public void Deduplicates_targets()
    {
        // The same connection chosen twice must not produce two posts to it.
        var post = Post(null,
            ("conn-fb", SocialPlatform.FacebookPage),
            ("conn-fb", SocialPlatform.FacebookPage));

        Assert.Single(post.Targets);
    }

    /* ---------------- due-ness ---------------- */

    [Fact]
    public void Publish_now_is_immediately_due()
    {
        // "Now" is a scheduled post with no time, not a separate path — so it
        // gets the same lease and retry handling as everything else.
        var post = Post(null);

        Assert.Equal(PublishStatus.Scheduled, post.Status);
        Assert.True(post.IsDue(DateTime.UtcNow));
    }

    [Fact]
    public void A_future_post_is_not_due_yet()
    {
        var post = Post(DateTime.UtcNow.AddHours(2));

        Assert.False(post.IsDue(DateTime.UtcNow));
        Assert.True(post.IsDue(DateTime.UtcNow.AddHours(3)));
    }

    [Fact]
    public void A_claimed_post_is_no_longer_due()
    {
        // Guards the worker: once claimed, status is Publishing and IsDue must
        // be false, or a second instance could pick it up.
        var post = Post(null);
        post.MarkClaimed("worker-a", DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(PublishStatus.Publishing, post.Status);
        Assert.False(post.IsDue(DateTime.UtcNow));
    }

    /* ---------------- status rollup ---------------- */

    [Fact]
    public void All_published_rolls_up_to_Published()
    {
        var post = Post(null,
            ("a", SocialPlatform.FacebookPage),
            ("b", SocialPlatform.InstagramBusiness));

        post.TargetFor("a")!.MarkPublished("ext-a", null);
        post.TargetFor("b")!.MarkPublished("ext-b", null);
        post.RecomputeStatus();

        Assert.Equal(PublishStatus.Published, post.Status);
        Assert.NotNull(post.CompletedAtUtc);
    }

    [Fact]
    public void Mixed_results_roll_up_to_PartiallyPublished()
    {
        var post = Post(null,
            ("a", SocialPlatform.FacebookPage),
            ("b", SocialPlatform.InstagramBusiness));

        post.TargetFor("a")!.MarkPublished("ext-a", null);
        post.TargetFor("b")!.MarkFailed("Aspect ratio not supported");
        post.RecomputeStatus();

        // A distinct state, not a flavour of Failed — retrying the two needs
        // opposite handling.
        Assert.Equal(PublishStatus.PartiallyPublished, post.Status);
    }

    [Fact]
    public void All_failed_rolls_up_to_Failed()
    {
        var post = Post(null,
            ("a", SocialPlatform.FacebookPage),
            ("b", SocialPlatform.InstagramBusiness));

        post.TargetFor("a")!.MarkFailed("nope");
        post.TargetFor("b")!.MarkFailed("nope");
        post.RecomputeStatus();

        Assert.Equal(PublishStatus.Failed, post.Status);
    }

    [Fact]
    public void A_skipped_target_does_not_make_the_post_a_failure()
    {
        // Nothing went wrong — one platform simply could not carry the media.
        var post = Post(null,
            ("a", SocialPlatform.FacebookPage),
            ("b", SocialPlatform.GoogleBusinessProfile));

        post.TargetFor("a")!.MarkPublished("ext-a", null);
        post.TargetFor("b")!.MarkSkipped("Google Business Profile cannot publish videos.");
        post.RecomputeStatus();

        Assert.Equal(PublishStatus.Published, post.Status);
    }

    [Fact]
    public void Outstanding_targets_keep_the_post_Publishing()
    {
        var post = Post(null,
            ("a", SocialPlatform.FacebookPage),
            ("b", SocialPlatform.InstagramBusiness));

        post.TargetFor("a")!.MarkPublished("ext-a", null);
        post.RecomputeStatus();

        Assert.Equal(PublishStatus.Publishing, post.Status);
        Assert.Null(post.CompletedAtUtc);
    }

    /* ---------------- retry: the one that must not be wrong ---------------- */

    [Fact]
    public void Retry_does_not_reset_a_target_that_already_published()
    {
        var post = Post(null,
            ("a", SocialPlatform.FacebookPage),
            ("b", SocialPlatform.InstagramBusiness));

        post.TargetFor("a")!.MarkPublished("ext-a", "https://fb/1");
        post.TargetFor("b")!.MarkFailed("temporary");
        post.RecomputeStatus();

        post.PrepareRetry();

        var succeeded = post.TargetFor("a")!;
        var failed = post.TargetFor("b")!;

        // This assertion is the whole point of the file. If it ever fails, the
        // next retry posts to that pharmacy's Facebook page a second time.
        Assert.Equal(PublishTargetStatus.Published, succeeded.Status);
        Assert.Equal("ext-a", succeeded.ExternalPostId);
        Assert.Equal("https://fb/1", succeeded.Permalink);

        Assert.Equal(PublishTargetStatus.Pending, failed.Status);
        Assert.Null(failed.Error);
    }

    [Fact]
    public void Retry_makes_the_post_due_immediately()
    {
        var post = Post(DateTime.UtcNow.AddHours(5));
        post.TargetFor("conn-fb")!.MarkFailed("nope");
        post.RecomputeStatus();

        post.PrepareRetry();

        Assert.Equal(PublishStatus.Scheduled, post.Status);
        Assert.Null(post.ScheduledForUtc);
        Assert.True(post.IsDue(DateTime.UtcNow));
        // The lease must be cleared, or the worker cannot claim it.
        Assert.Null(post.LeaseHolder);
    }

    [Fact]
    public void Cannot_retry_a_fully_published_post()
    {
        var post = Post(null);
        post.TargetFor("conn-fb")!.MarkPublished("ext", null);
        post.RecomputeStatus();

        var ex = Assert.Throws<DomainException>(() => post.PrepareRetry());
        Assert.Contains("already", ex.Message);
    }

    [Fact]
    public void Cannot_retry_a_post_that_is_publishing()
    {
        var post = Post(null);
        post.MarkClaimed("worker-a", DateTime.UtcNow.AddMinutes(5));

        Assert.Throws<DomainException>(() => post.PrepareRetry());
    }

    [Fact]
    public void ResetForManualRetry_leaves_a_published_target_alone()
    {
        // Belt and braces: the guard exists on the target as well as on the
        // post, because the consequence of missing it reaches real customers.
        var post = Post(null);
        var target = post.TargetFor("conn-fb")!;
        target.MarkPublished("ext", null);

        target.ResetForManualRetry();

        Assert.Equal(PublishTargetStatus.Published, target.Status);
    }

    [Fact]
    public void A_published_target_can_never_be_retried()
    {
        var post = Post(null);
        var target = post.TargetFor("conn-fb")!;
        target.MarkPublished("ext", null);

        Assert.False(target.CanRetry(maxAttempts: 99));
    }

    [Fact]
    public void Attempts_are_capped()
    {
        var post = Post(null);
        var target = post.TargetFor("conn-fb")!;

        for (var i = 0; i < 3; i++)
        {
            target.MarkPublishing();
            target.MarkFailed("still no");
        }

        Assert.False(target.CanRetry(maxAttempts: 3));
        Assert.True(target.CanRetry(maxAttempts: 4));
    }

    /* ---------------- cancel ---------------- */

    [Fact]
    public void Can_cancel_a_scheduled_post()
    {
        var post = Post(DateTime.UtcNow.AddHours(1));
        post.Cancel();

        Assert.Equal(PublishStatus.Cancelled, post.Status);
        Assert.False(post.IsDue(DateTime.UtcNow.AddHours(2)));
    }

    [Fact]
    public void Cannot_cancel_something_that_already_went_out()
    {
        var post = Post(null);
        post.TargetFor("conn-fb")!.MarkPublished("ext", null);
        post.RecomputeStatus();

        Assert.Throws<DomainException>(() => post.Cancel());
    }

    [Fact]
    public void Cancelling_survives_a_status_recompute()
    {
        // A cancelled post must not be quietly resurrected by the worker
        // recomputing its status.
        var post = Post(DateTime.UtcNow.AddHours(1));
        post.Cancel();
        post.RecomputeStatus();

        Assert.Equal(PublishStatus.Cancelled, post.Status);
    }

    /* ---------------- recovery and automatic retry ---------------- */

    [Fact]
    public void A_target_left_mid_call_is_marked_uncertain_not_resent()
    {
        // The worker died between sending and recording. The post may be
        // live; the reclaiming worker must not send it again.
        var post = Post(null,
            ("a", SocialPlatform.FacebookPage),
            ("b", SocialPlatform.InstagramBusiness));
        post.MarkClaimed("worker-a", DateTime.UtcNow.AddMinutes(5));
        post.TargetFor("a")!.MarkPublishing();

        Assert.Equal(1, post.ResolveInterruptedAttempts());

        Assert.Equal(PublishTargetStatus.Uncertain, post.TargetFor("a")!.Status);
        Assert.Equal(PublishTargetStatus.Pending, post.TargetFor("b")!.Status);
    }

    [Fact]
    public void Uncertain_rolls_up_with_the_failures_so_Retry_is_offered()
    {
        var post = Post(null,
            ("a", SocialPlatform.FacebookPage),
            ("b", SocialPlatform.InstagramBusiness));
        post.TargetFor("a")!.MarkPublished("ext-a", null);
        post.TargetFor("b")!.MarkUncertain("may have gone out");
        post.RecomputeStatus();

        Assert.Equal(PublishStatus.PartiallyPublished, post.Status);
    }

    [Fact]
    public void A_manual_retry_starts_the_attempt_count_again()
    {
        // The bug: after three automatic attempts the target was reset to
        // Pending but still over the ceiling, so the worker skipped it and the
        // post sat in Publishing for ever.
        var post = Post(null);
        var target = post.TargetFor("conn-fb")!;
        for (var i = 0; i < 3; i++) { target.MarkPublishing(); target.MarkFailed("no"); }
        post.RecomputeStatus();

        post.PrepareRetry();

        Assert.Equal(PublishTargetStatus.Pending, target.Status);
        Assert.Equal(0, target.AttemptCount);
        Assert.True(target.CanRetry(maxAttempts: 3));
    }

    [Fact]
    public void A_manual_retry_includes_uncertain_targets_but_never_published_ones()
    {
        var post = Post(null,
            ("a", SocialPlatform.FacebookPage),
            ("b", SocialPlatform.InstagramBusiness));
        post.TargetFor("a")!.MarkPublished("ext-a", null);
        post.TargetFor("b")!.MarkUncertain("may have gone out");
        post.RecomputeStatus();

        post.PrepareRetry();

        Assert.Equal(PublishTargetStatus.Published, post.TargetFor("a")!.Status);
        Assert.Equal(PublishTargetStatus.Pending, post.TargetFor("b")!.Status);
    }

    [Fact]
    public void An_automatic_retry_waits_out_its_backoff()
    {
        var post = Post(null);
        post.MarkClaimed("worker-a", DateTime.UtcNow.AddMinutes(5));
        post.TargetFor("conn-fb")!.MarkRetryPending("rate limited");

        var at = DateTime.UtcNow.AddMinutes(2);
        post.ScheduleAutomaticRetry(at);

        Assert.Equal(PublishStatus.Scheduled, post.Status);
        Assert.Null(post.LeaseHolder);
        Assert.Null(post.ScheduledForUtc);   // the pharmacy's own choice is untouched
        Assert.False(post.IsDue(DateTime.UtcNow));
        Assert.True(post.IsDue(at.AddSeconds(1)));
    }

    [Fact]
    public void Cancelling_after_some_accounts_published_closes_it_as_what_it_is()
    {
        // Waiting on a retry after Facebook went out: cancel stops the rest,
        // but the post must not read "Cancelled" as though nothing was sent.
        var post = Post(null,
            ("a", SocialPlatform.FacebookPage),
            ("b", SocialPlatform.InstagramBusiness));
        post.MarkClaimed("worker-a", DateTime.UtcNow.AddMinutes(5));
        post.TargetFor("a")!.MarkPublished("ext-a", null);
        post.TargetFor("b")!.MarkRetryPending("rate limited");
        post.ScheduleAutomaticRetry(DateTime.UtcNow.AddMinutes(2));

        post.Cancel();

        Assert.Equal(PublishStatus.Published, post.Status);
        Assert.Equal(PublishTargetStatus.Skipped, post.TargetFor("b")!.Status);
        Assert.False(post.IsDue(DateTime.UtcNow.AddHours(1)));
    }

    [Fact]
    public void Retry_pending_and_uncertain_never_touch_a_published_target()
    {
        var post = Post(null);
        var target = post.TargetFor("conn-fb")!;
        target.MarkPublished("ext", null);

        target.MarkRetryPending("x");
        target.MarkUncertain("y");

        Assert.Equal(PublishTargetStatus.Published, target.Status);
    }

    [Fact]
    public void Retry_cannot_pull_a_scheduled_post_forward()
    {
        // Retry makes a post due now. On a post scheduled for next week that
        // would publish it this minute.
        var post = Post(DateTime.UtcNow.AddDays(7));

        Assert.Throws<DomainException>(() => post.PrepareRetry());
        Assert.NotNull(post.ScheduledForUtc);
    }
}
