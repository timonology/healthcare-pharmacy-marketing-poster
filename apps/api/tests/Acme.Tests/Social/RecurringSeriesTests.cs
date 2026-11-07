using Acme.Application.Messaging;
using Acme.Application.Social;
using Acme.Application.Videos;
using Acme.Domain.Posters;
using Acme.Domain.Social;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// Recurring series: the dates, the late rule, pausing, the plan, and that a
/// broken account fails one post rather than the series.
/// </summary>
/// <remarks>
/// The atomic parts — two schedulers making one occurrence, two workers
/// claiming it once — are Mongo's and are tested against it in
/// <c>RecurringSeriesConcurrencyTests</c>. Everything here is in memory.
/// </remarks>
public class RecurringSeriesTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeOnly Nine = new(9, 0);

    private static DateTime Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    /* ---------------- dates and clock changes ---------------- */

    [Fact]
    public void Nine_oclock_stays_nine_oclock_when_the_clocks_go_forward()
    {
        // Sunday 29 March 2026, 01:00 GMT → 02:00 BST.
        var daily = new Cadence(CadenceKind.Daily, Nine);
        var start = new DateOnly(2026, 3, 1);

        var saturday = CadenceSchedule.NextAfter(daily, London, start, null, Utc(2026, 3, 27, 12));
        var sunday = CadenceSchedule.NextAfter(daily, London, start, null, saturday!.Value);

        Assert.Equal(Utc(2026, 3, 28, 9), saturday);   // GMT: 09:00 local is 09:00 UTC
        Assert.Equal(Utc(2026, 3, 29, 8), sunday);     // BST: 09:00 local is 08:00 UTC
    }

    [Fact]
    public void Nine_oclock_stays_nine_oclock_when_the_clocks_go_back()
    {
        // Sunday 25 October 2026, 02:00 BST → 01:00 GMT.
        var weekly = new Cadence(CadenceKind.Weekly, Nine, new[] { DayOfWeek.Saturday, DayOfWeek.Sunday });
        var start = new DateOnly(2026, 10, 1);

        var upcoming = CadenceSchedule.Upcoming(weekly, London, start, null, Utc(2026, 10, 23, 12), 2);

        Assert.Equal(Utc(2026, 10, 24, 8), upcoming[0]);  // Sat, still BST
        Assert.Equal(Utc(2026, 10, 25, 9), upcoming[1]);  // Sun, now GMT
    }

    [Fact]
    public void A_time_that_does_not_exist_moves_forward_by_the_skipped_hour()
    {
        // 01:30 on 29 March 2026 never happens in London; 02:30 BST does.
        var at = CadenceSchedule.ToUtc(new DateOnly(2026, 3, 29), new TimeOnly(1, 30), London);
        Assert.Equal(Utc(2026, 3, 29, 1, 30), at);   // 02:30 BST
    }

    [Fact]
    public void A_time_that_happens_twice_posts_once_at_the_first()
    {
        // 01:30 on 25 October 2026 happens in BST and again in GMT.
        var at = CadenceSchedule.ToUtc(new DateOnly(2026, 10, 25), new TimeOnly(1, 30), London);
        Assert.Equal(Utc(2026, 10, 25, 0, 30), at);  // the BST one
    }

    [Fact]
    public void Every_n_days_counts_from_the_start_date_and_stops_at_the_end_date()
    {
        var everyThree = new Cadence(CadenceKind.EveryNDays, Nine, IntervalDays: 3);
        var start = new DateOnly(2026, 11, 2);
        var end = new DateOnly(2026, 11, 9);

        var all = CadenceSchedule.Upcoming(everyThree, London, start, end, Utc(2026, 11, 1, 0), 10);

        Assert.Equal(new[] { Utc(2026, 11, 2, 9), Utc(2026, 11, 5, 9), Utc(2026, 11, 8, 9) }, all);
    }

    [Fact]
    public void Specific_dates_run_out()
    {
        var dates = new Cadence(CadenceKind.SpecificDates, Nine,
            Dates: new[] { new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 8) });
        var start = new DateOnly(2026, 11, 1);

        Assert.Equal(Utc(2026, 12, 8, 9),
            CadenceSchedule.NextAfter(dates, London, start, null, Utc(2026, 12, 1, 9)));
        Assert.Null(CadenceSchedule.NextAfter(dates, London, start, null, Utc(2026, 12, 8, 9)));
    }

    [Fact]
    public void Cadence_is_described_in_words()
    {
        Assert.Equal("Every Mon and Thu at 9:00",
            SeriesWording.Describe(new Cadence(CadenceKind.Weekly, Nine, new[] { DayOfWeek.Thursday, DayOfWeek.Monday }).Normalised()));
        Assert.Equal("Every day at 9:00", SeriesWording.Describe(new Cadence(CadenceKind.Daily, Nine)));
        Assert.Equal("Every 3 days at 18:30",
            SeriesWording.Describe(new Cadence(CadenceKind.EveryNDays, new TimeOnly(18, 30), IntervalDays: 3)));
        Assert.Equal("Wed 28 Oct, 09:00 UK", SeriesWording.Time(Utc(2026, 10, 28, 9), London, "Europe/London").Label);
    }

    /* ---------------- late occurrences ---------------- */

    private static RecurringSeries Daily(DateTime createdAtUtc, string ownerId = "owner-1") =>
        RecurringSeries.Create(
            ownerId, "Flu clinic", PublishSourceKind.Poster, "poster-1", "Flu clinic", "Book now",
            new[] { new SeriesDestination("fb-1", SocialPlatform.FacebookPage) },
            new Cadence(CadenceKind.Daily, Nine), "Europe/London",
            DateOnly.FromDateTime(createdAtUtc), null, SeriesNotify.All, createdAtUtc);

    [Fact]
    public void An_occurrence_under_two_hours_late_is_posted()
    {
        var s = Daily(Utc(2026, 11, 2, 6));            // next: 2 Nov 09:00 GMT
        var plan = s.PlanDue(Utc(2026, 11, 2, 10, 30));

        Assert.Equal(Utc(2026, 11, 2, 9), plan.PostAtUtc);
        Assert.Equal(0, plan.Missed);
        Assert.Equal(Utc(2026, 11, 3, 9), plan.NextUtc);
    }

    [Fact]
    public void An_occurrence_more_than_two_hours_late_is_skipped_and_the_next_scheduled()
    {
        var s = Daily(Utc(2026, 11, 2, 6));
        var plan = s.PlanDue(Utc(2026, 11, 2, 11, 1));

        Assert.Null(plan.PostAtUtc);
        Assert.Equal(1, plan.Missed);
        Assert.Equal(Utc(2026, 11, 3, 9), plan.NextUtc);
    }

    [Fact]
    public void A_week_down_never_bursts_a_weeks_posts()
    {
        var s = Daily(Utc(2026, 11, 2, 6));

        // Back up at 10:00 on the 9th: the 9th's post is an hour late, the
        // seven before it are days late.
        var plan = s.PlanDue(Utc(2026, 11, 9, 10));

        Assert.Equal(Utc(2026, 11, 9, 9), plan.PostAtUtc);   // one post, the fresh one
        Assert.Equal(7, plan.Missed);
        Assert.Equal(Utc(2026, 11, 10, 9), plan.NextUtc);
    }

    [Fact]
    public void Occurrence_ids_are_stable_for_the_same_series_and_instant()
    {
        var s = Daily(Utc(2026, 11, 2, 6));
        Assert.Equal(s.OccurrenceId(Utc(2026, 11, 2, 9)), s.OccurrenceId(Utc(2026, 11, 2, 9)));
        Assert.NotEqual(s.OccurrenceId(Utc(2026, 11, 2, 9)), s.OccurrenceId(Utc(2026, 11, 3, 9)));
    }

    /* ---------------- scheduler, pause, plan ---------------- */

    [Fact]
    public async Task A_due_series_queues_one_post_and_moves_on()
    {
        var h = new Harness(SubscriptionTier.Starter);
        var s = await h.AddSeries();

        await h.Service.ProcessDueAsync(s, s.NextOccurrenceUtc!.Value.AddMinutes(1), default);

        var post = Assert.Single(h.Occurrences.Items.Values);
        Assert.Equal(s.Id, post.SeriesId);
        Assert.Equal(PublishStatus.Scheduled, post.Status);
        Assert.Equal(SeriesStatus.Active, h.Series.Items[s.Id].Status);
        Assert.True(h.Series.Items[s.Id].NextOccurrenceUtc > post.OccurrenceAtUtc);
    }

    [Fact]
    public async Task Running_the_same_due_occurrence_twice_queues_it_once()
    {
        var h = new Harness(SubscriptionTier.Starter);
        var s = await h.AddSeries();
        var due = s.NextOccurrenceUtc!.Value;

        // Two schedulers, each with its own copy read before either saved.
        var copyA = Clone(h, s);
        var copyB = Clone(h, s);
        await h.Service.ProcessDueAsync(copyA, due.AddMinutes(1), default);
        await h.Service.ProcessDueAsync(copyB, due.AddMinutes(1), default);

        Assert.Single(h.Occurrences.Items.Values);
    }

    [Fact]
    public async Task Pausing_stops_future_posts_and_leaves_history()
    {
        var h = new Harness(SubscriptionTier.Starter);
        var s = await h.AddSeries();
        var first = s.NextOccurrenceUtc!.Value;

        // One occurrence that went out…
        await h.Service.ProcessDueAsync(s, first.AddMinutes(1), default);
        var sent = h.Occurrences.Items.Values.Single();
        sent.MarkClaimed("worker", first.AddMinutes(5));
        sent.Targets[0].MarkPublishing();
        sent.Targets[0].MarkPublished("fb-post-1", null);
        sent.RecomputeStatus();

        // …and one queued but not yet started.
        var second = h.Series.Items[s.Id].NextOccurrenceUtc!.Value;
        await h.Service.ProcessDueAsync(h.Series.Items[s.Id], second.AddMinutes(1), default);
        var queued = h.Occurrences.Items.Values.Single(p => p.Id != sent.Id);

        var paused = await h.Service.PauseAsync(h.Pharmacy.Id, s.Id, default);

        Assert.Equal(SeriesStatus.Paused, paused.Value!.Status);
        Assert.Null(h.Series.Items[s.Id].NextOccurrenceUtc);
        Assert.Equal(PublishStatus.Published, sent.Status);       // history stays
        Assert.Equal(PublishStatus.Cancelled, queued.Status);     // the future one does not go

        // And the scheduler finds nothing to do for it.
        var before = h.Occurrences.Items.Count;
        await h.Service.ProcessDueAsync(h.Series.Items[s.Id], second.AddDays(3), default);
        Assert.Equal(before, h.Occurrences.Items.Count);
    }

    [Fact]
    public async Task A_queued_occurrence_never_picked_up_is_skipped_once_two_hours_late()
    {
        var h = new Harness(SubscriptionTier.Starter);
        var s = await h.AddSeries();
        var at = s.NextOccurrenceUtc!.Value;
        await h.Service.ProcessDueAsync(s, at.AddMinutes(1), default);
        var queued = h.Occurrences.Items.Values.Single();

        // The server stopped before the publish worker claimed it.
        await h.Service.RunDueAsync(at.AddHours(2).AddMinutes(1), default);

        Assert.Equal(PublishStatus.Cancelled, queued.Status);
    }

    [Fact]
    public async Task An_occurrence_the_pharmacy_rescheduled_is_judged_by_its_new_time()
    {
        var h = new Harness(SubscriptionTier.Starter);
        var s = await h.AddSeries();
        var at = s.NextOccurrenceUtc!.Value;
        await h.Service.ProcessDueAsync(s, at.AddMinutes(1), default);
        var queued = h.Occurrences.Items.Values.Single();

        // Moved from 09:00 to 14:00 on the Scheduled tab.
        queued.Reschedule(at.AddHours(5));
        await h.Service.RunDueAsync(at.AddHours(3), default);

        Assert.Equal(PublishStatus.Scheduled, queued.Status);
    }

    [Fact]
    public async Task A_manual_retry_of_an_old_occurrence_is_not_swept_up_as_stale()
    {
        var h = new Harness(SubscriptionTier.Starter);
        var s = await h.AddSeries();
        var at = s.NextOccurrenceUtc!.Value;
        await h.Service.ProcessDueAsync(s, at.AddMinutes(1), default);
        var failed = h.Occurrences.Items.Values.Single();
        failed.MarkClaimed("worker", DateTime.UtcNow.AddMinutes(5));
        failed.Targets[0].MarkPublishing();
        failed.Targets[0].MarkFailed("Temporary problem.");
        failed.RecomputeStatus();

        // A day later the pharmacy presses Retry: attempt counts start again.
        failed.PrepareRetry();
        await h.Service.RunDueAsync(at.AddDays(1), default);

        Assert.Equal(PublishStatus.Scheduled, failed.Status);
    }

    [Fact]
    public async Task A_poster_no_longer_published_pauses_the_series_and_emails_once()
    {
        var h = new Harness(SubscriptionTier.Starter);
        var s = await h.AddSeries();
        h.Poster.Archive();

        await h.Service.ProcessDueAsync(s, s.NextOccurrenceUtc!.Value.AddMinutes(1), default);

        Assert.Empty(h.Occurrences.Items);
        Assert.Equal(SeriesStatus.Paused, h.Series.Items[s.Id].Status);
        Assert.True(h.Series.Items[s.Id].PausedBySystem);
        Assert.Single(h.Notifier.Paused);

        // Paused is never due again, so there is no second email.
        await h.Service.RunDueAsync(DateTime.UtcNow.AddDays(30), default);
        Assert.Single(h.Notifier.Paused);
    }

    [Fact]
    public async Task Dropping_to_free_pauses_the_series()
    {
        var h = new Harness(SubscriptionTier.Starter);
        var s = await h.AddSeries();
        h.Pharmacy.ChangeTier(SubscriptionTier.Free);

        await h.Service.ProcessDueAsync(s, s.NextOccurrenceUtc!.Value.AddMinutes(1), default);

        Assert.Empty(h.Occurrences.Items);
        Assert.Equal(SeriesStatus.Paused, h.Series.Items[s.Id].Status);
        Assert.Contains("Free", h.Series.Items[s.Id].StatusReason);
    }

    [Fact]
    public async Task Free_cannot_create_a_series()
    {
        var h = new Harness(SubscriptionTier.Free);
        var result = await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), default);
        Assert.Equal(Acme.Application.Common.ResultKind.Forbidden, result.Kind);
    }

    [Fact]
    public async Task Starter_is_limited_to_two_active_series_on_create()
    {
        var h = new Harness(SubscriptionTier.Starter);
        Assert.True((await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), default)).IsSuccess);
        Assert.True((await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), default)).IsSuccess);

        var third = await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), default);

        Assert.Equal(Acme.Application.Common.ResultKind.Forbidden, third.Kind);
        Assert.Contains("2 active", third.Error);
    }

    [Fact]
    public async Task Pro_allows_twenty_active_series()
    {
        var h = new Harness(SubscriptionTier.Pro);
        for (var i = 0; i < 20; i++)
            Assert.True((await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), default)).IsSuccess);
        Assert.Equal(Acme.Application.Common.ResultKind.Forbidden,
            (await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), default)).Kind);
    }

    [Fact]
    public async Task Resuming_is_checked_against_the_limit_too()
    {
        var h = new Harness(SubscriptionTier.Starter);
        var a = (await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), default)).Value!;
        await h.Service.PauseAsync(h.Pharmacy.Id, a.Id, default);
        await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), default);
        await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), default);

        var resumed = await h.Service.ResumeAsync(h.Pharmacy.Id, a.Id, default);

        Assert.Equal(Acme.Application.Common.ResultKind.Forbidden, resumed.Kind);
    }

    [Fact]
    public async Task Creating_does_not_post_unless_also_post_now_is_ticked()
    {
        var h = new Harness(SubscriptionTier.Starter);
        await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), default);
        Assert.Empty(h.Occurrences.Items);

        await h.Service.CreateAsync(h.Pharmacy.Id, h.Request() with { PostNow = true }, default);
        Assert.Single(h.Occurrences.Items);
    }

    [Fact]
    public async Task Editing_changes_future_occurrences_only()
    {
        var h = new Harness(SubscriptionTier.Starter);
        var created = (await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), default)).Value!;
        var s = h.Series.Items[created.Id];

        // One already sent.
        await h.Service.ProcessDueAsync(s, s.NextOccurrenceUtc!.Value.AddMinutes(1), default);
        var sent = h.Occurrences.Items.Values.Single();
        sent.MarkClaimed("worker", DateTime.UtcNow.AddMinutes(5));
        sent.Targets[0].MarkPublishing();
        sent.Targets[0].MarkPublished("fb-1", null);
        sent.RecomputeStatus();

        var edited = await h.Service.UpdateAsync(h.Pharmacy.Id, s.Id,
            h.Request() with { Caption = "New words", Cadence = new CadenceRequest(CadenceKind.Daily, "18:00") }, default);

        Assert.True(edited.IsSuccess, edited.Error);
        Assert.Equal("Book now", sent.Caption);                   // the sent one is untouched
        Assert.Equal("New words", h.Series.Items[s.Id].Caption);
        Assert.Equal("18:00", edited.Value!.Time);
    }

    /* ---------------- a broken account fails one post, not the series ---------------- */

    [Fact]
    public async Task An_account_that_needs_reconnecting_fails_that_occurrence_only()
    {
        var h = new Harness(SubscriptionTier.Starter);
        var instagram = h.Connect(SocialPlatform.InstagramBusiness);
        var s = await h.AddSeries(extra: instagram);

        // Facebook's token is revoked after the series was set up.
        h.Facebook.MarkNeedsReauth("Token expired.");

        var first = s.NextOccurrenceUtc!.Value;
        await h.Service.ProcessDueAsync(s, first.AddMinutes(1), default);
        var occurrence = h.Occurrences.Items.Values.Single();

        // Both accounts are on it: the broken one is failed, not silently dropped.
        Assert.Equal(2, occurrence.Targets.Count);

        // The existing publish worker sends it.
        occurrence.MarkClaimed("worker", DateTime.UtcNow.AddMinutes(5));
        h.InstagramPublisher.Next(new PublishOutcome(true, "ig-1", null, null));
        await h.Publisher.ExecuteAsync(occurrence, TimeSpan.FromMinutes(5), default);

        Assert.Equal(PublishStatus.PartiallyPublished, occurrence.Status);
        Assert.Equal(PublishTargetStatus.Failed, occurrence.TargetFor(h.Facebook.Id)!.Status);
        Assert.Contains("reconnect", occurrence.TargetFor(h.Facebook.Id)!.Error);
        Assert.Equal(PublishTargetStatus.Published, occurrence.TargetFor(instagram.Id)!.Status);

        // The series carries on.
        var series = h.Series.Items[s.Id];
        Assert.Equal(SeriesStatus.Active, series.Status);
        await h.Service.ProcessDueAsync(series, series.NextOccurrenceUtc!.Value.AddMinutes(1), default);
        Assert.Equal(2, h.Occurrences.Items.Count);

        // And the pharmacy is told what needs doing, once.
        await h.Service.ReportOutcomesAsync(DateTime.UtcNow, default);
        await h.Service.ReportOutcomesAsync(DateTime.UtcNow, default);
        var mail = Assert.Single(h.Mail.Sent);
        Assert.StartsWith("Reconnect needed", mail.Subject);
    }

    [Fact]
    public async Task A_removed_account_is_left_off_and_with_none_left_the_series_pauses()
    {
        var h = new Harness(SubscriptionTier.Starter);
        var instagram = h.Connect(SocialPlatform.InstagramBusiness);
        var s = await h.AddSeries(extra: instagram);

        // Instagram disconnected and removed: not a reconnect, gone.
        h.Connections.Items.Remove(instagram.Id);
        await h.Service.ProcessDueAsync(s, s.NextOccurrenceUtc!.Value.AddMinutes(1), default);

        var occurrence = Assert.Single(h.Occurrences.Items.Values);
        Assert.Equal(new[] { h.Facebook.Id }, occurrence.Targets.Select(t => t.ConnectionId));

        // Facebook goes too: nothing left to post to, so it pauses once.
        h.Connections.Items.Remove(h.Facebook.Id);
        var series = h.Series.Items[s.Id];
        await h.Service.ProcessDueAsync(series, series.NextOccurrenceUtc!.Value.AddMinutes(1), default);

        Assert.Equal(SeriesStatus.Paused, h.Series.Items[s.Id].Status);
        Assert.Single(h.Occurrences.Items.Values);
        Assert.Single(h.Notifier.Paused);
    }

    [Fact]
    public async Task Editing_keeps_an_account_that_needs_reconnecting_but_refuses_adding_one()
    {
        var h = new Harness(SubscriptionTier.Starter);
        var instagram = h.Connect(SocialPlatform.InstagramBusiness);
        var s = await h.AddSeries();                 // Facebook only
        h.Facebook.MarkNeedsReauth("Token expired.");

        // Changing the time with Facebook still on it is fine…
        var keep = await h.Service.UpdateAsync(h.Pharmacy.Id, s.Id,
            h.Request() with { Cadence = new CadenceRequest(CadenceKind.Daily, "18:00") }, default);
        Assert.True(keep.IsSuccess, keep.Error);

        // …but a broken account cannot be added to a series that did not have it.
        instagram.MarkNeedsReauth("Token expired.");
        var add = await h.Service.UpdateAsync(h.Pharmacy.Id, s.Id,
            h.Request() with { ConnectionIds = new[] { h.Facebook.Id, instagram.Id } }, default);
        Assert.Equal(Acme.Application.Common.ResultKind.Invalid, add.Kind);
    }

    [Fact]
    public async Task A_poster_series_cannot_go_to_youtube()
    {
        var h = new Harness(SubscriptionTier.Starter);
        var youtube = h.Connect(SocialPlatform.YouTubeChannel);

        var result = await h.Service.CreateAsync(h.Pharmacy.Id,
            h.Request() with { ConnectionIds = new[] { youtube.Id } }, default);

        Assert.Equal(Acme.Application.Common.ResultKind.Invalid, result.Kind);
        Assert.Contains("only takes videos", result.Error);
    }

    [Fact]
    public async Task TikTok_cannot_be_part_of_a_recurring_post()
    {
        // TikTok wants each post's audience chosen by a person.
        var h = new Harness(SubscriptionTier.Starter);
        var tikTok = h.Connect(SocialPlatform.TikTokAccount);

        var result = await h.Service.CreateAsync(h.Pharmacy.Id,
            h.Request() with { ConnectionIds = new[] { tikTok.Id } }, default);

        Assert.Equal(Acme.Application.Common.ResultKind.Invalid, result.Kind);
        Assert.Contains("TikTok", result.Error);
    }

    [Fact]
    public async Task X_cannot_be_part_of_a_recurring_post()
    {
        // X refuses repeated text, and a series repeats its caption.
        var h = new Harness(SubscriptionTier.Starter);
        var x = h.Connect(SocialPlatform.XAccount);

        var result = await h.Service.CreateAsync(h.Pharmacy.Id,
            h.Request() with { ConnectionIds = new[] { x.Id } }, default);

        Assert.Equal(Acme.Application.Common.ResultKind.Invalid, result.Kind);
        Assert.Contains("X cannot be part of a recurring post", result.Error);
    }

    [Fact]
    public async Task Problems_only_does_not_email_a_successful_post()
    {
        var h = new Harness(SubscriptionTier.Starter, notify: SeriesNotify.ProblemsOnly);
        var s = await h.AddSeries();
        await h.Service.ProcessDueAsync(s, s.NextOccurrenceUtc!.Value.AddMinutes(1), default);
        var occurrence = h.Occurrences.Items.Values.Single();
        occurrence.MarkClaimed("worker", DateTime.UtcNow.AddMinutes(5));
        h.FacebookPublisher.Next(new PublishOutcome(true, "fb-1", null, null));
        await h.Publisher.ExecuteAsync(occurrence, TimeSpan.FromMinutes(5), default);

        await h.Service.ReportOutcomesAsync(DateTime.UtcNow, default);

        Assert.Equal(PublishStatus.Published, occurrence.Status);
        Assert.Empty(h.Mail.Sent);
    }

    [Fact]
    public void The_series_emails_escape_what_the_pharmacy_typed()
    {
        var user = User.Register(Email.Create("p@example.test"), "<b>Sam</b>", PasswordHash.FromHash("x"));
        var s = RecurringSeries.Create(
            user.Id, "<script>x</script>", PublishSourceKind.Poster, "poster-1", "P", "",
            new[] { new SeriesDestination("fb-1", SocialPlatform.FacebookPage) },
            new Cadence(CadenceKind.Daily, Nine), "Europe/London",
            DateOnly.FromDateTime(DateTime.UtcNow), null, SeriesNotify.All, DateTime.UtcNow);

        var mail = SeriesEmails.Paused(user, s, "Because.", new SeriesEmails.AppLinks("https://a/r", "https://a/c", "https://a/p"));

        Assert.DoesNotContain("<script>", mail.HtmlBody);
        Assert.DoesNotContain("<b>Sam</b>", mail.HtmlBody);
    }

    /* ================================================================ *
     * Harness and fakes
     * ================================================================ */

    private static RecurringSeries Clone(Harness h, RecurringSeries s)
    {
        // A second reader's copy: same state, separate object.
        var copy = Acme.Tests.Social.RecurringSeriesTests.Copy(s);
        return copy;
    }

    internal static RecurringSeries Copy(RecurringSeries s)
    {
        var copy = RecurringSeries.Create(
            s.OwnerId, s.Name, s.SourceKind, s.SourceId, s.SourceName, s.Caption, s.Destinations,
            s.Cadence, s.TimeZoneId, s.StartDate, s.EndDate, s.Notify, s.CreatedAtUtc);
        // Same identity and position as the original.
        typeof(RecurringSeries).GetProperty(nameof(RecurringSeries.Id))!.SetValue(copy, s.Id);
        typeof(RecurringSeries).GetProperty(nameof(RecurringSeries.Revision))!.SetValue(copy, s.Revision);
        typeof(RecurringSeries).GetProperty(nameof(RecurringSeries.NextOccurrenceUtc))!.SetValue(copy, s.NextOccurrenceUtc);
        return copy;
    }

    private sealed class Harness
    {
        public User Pharmacy { get; }
        public Poster Poster { get; }
        public SocialConnection Facebook { get; }
        public FakeSeries Series { get; } = new();
        public FakeOccurrences Occurrences { get; } = new();
        public FakeConnections Connections { get; } = new();
        public RecordingEmail Mail { get; } = new();
        public RecordingNotifier Notifier { get; }
        public FakePublisher FacebookPublisher { get; } = new(SocialPlatform.FacebookPage);
        public FakePublisher InstagramPublisher { get; } = new(SocialPlatform.InstagramBusiness);
        private readonly SeriesNotify _notify;

        public Harness(SubscriptionTier tier, SeriesNotify notify = SeriesNotify.All)
        {
            _notify = notify;
            Pharmacy = User.Register(Email.Create($"p-{Guid.NewGuid():N}@example.test"), "Sam Patel", PasswordHash.FromHash("x"));
            Pharmacy.ChangeTier(tier);
            Poster = Poster.Create(Pharmacy.Id, "Flu clinic", "{}");
            Poster.Publish();
            Facebook = Connect(SocialPlatform.FacebookPage);
            Notifier = new RecordingNotifier(new SeriesNotifier(
                Mail, OAuthFlowTests.UsersOf.These(Pharmacy), Connections, new Links(),
                NullLogger<SeriesNotifier>.Instance));
        }

        public RecurringSeriesService Service => new(
            Series,
            Occurrences,
            Connections,
            OAuthFlowTests.UsersOf.These(Pharmacy),
            PublishExecutionTests.PostersOf.These(new List<Poster> { Poster }),
            PublishExecutionTests.Unused<IVideoRepository>.Instance,
            new Media(),
            Notifier,
            NullLogger<RecurringSeriesService>.Instance);

        public PublishService Publisher => new(
            new Posts(Occurrences),
            Connections,
            OAuthFlowTests.UsersOf.These(Pharmacy),
            new PlainCipher(),
            new Media(),
            PublishExecutionTests.PostersOf.These(new List<Poster> { Poster }),
            new ISocialPublisher[] { FacebookPublisher, InstagramPublisher },
            NullLogger<PublishService>.Instance);

        public SocialConnection Connect(SocialPlatform platform)
        {
            var c = SocialConnection.Create(
                Pharmacy.Id, platform, $"ext-{Guid.NewGuid():N}", platform.ToString(),
                EncryptedSecret.Create("token", "n", "t", 1), null, null);
            Connections.Items[c.Id] = c;
            return c;
        }

        public SeriesRequest Request() => new(
            "Flu clinic weekly", PublishSourceKind.Poster, Poster.Id, "Book now",
            new[] { Facebook.Id },
            new CadenceRequest(CadenceKind.Weekly, "09:00", new[] { DayOfWeek.Monday, DayOfWeek.Thursday }),
            "Europe/London",
            DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            Notify: _notify);

        public async Task<RecurringSeries> AddSeries(SocialConnection? extra = null)
        {
            var ids = extra is null ? new[] { Facebook.Id } : new[] { Facebook.Id, extra.Id };
            var result = await Service.CreateAsync(Pharmacy.Id, Request() with { ConnectionIds = ids }, default);
            Assert.True(result.IsSuccess, result.Error);
            return Series.Items[result.Value!.Id];
        }
    }

    private sealed class Links : Acme.Application.Auth.IAuthLinkBuilder
    {
        public string BuildPasswordResetLink(string rawToken) => "https://app.test/reset";
        public string BuildEmailVerificationLink(string rawToken) => "https://app.test/verify";
        public string BuildAppLink(string path = "/dashboard") => $"https://app.test/{path.TrimStart('/')}";
    }

    internal sealed class RecordingEmail : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = new();
        public Task<EmailDispatchResult> SendAsync(EmailMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.FromResult(new EmailDispatchResult(1, 0, null));
        }
    }

    internal sealed class RecordingNotifier(ISeriesNotifier inner) : ISeriesNotifier
    {
        public List<string> Paused { get; } = new();

        public Task OccurrenceFinishedAsync(RecurringSeries series, ScheduledPost occurrence, CancellationToken ct) =>
            inner.OccurrenceFinishedAsync(series, occurrence, ct);

        public Task SeriesPausedAsync(RecurringSeries series, string reason, CancellationToken ct)
        {
            Paused.Add(reason);
            return inner.SeriesPausedAsync(series, reason, ct);
        }
    }

    internal sealed class FakeSeries : IRecurringSeriesRepository
    {
        public Dictionary<string, RecurringSeries> Items { get; } = new();
        private readonly Dictionary<string, int> _revisions = new();

        public Task<RecurringSeries?> FindByIdAsync(string id, CancellationToken ct) =>
            Task.FromResult(Items.GetValueOrDefault(id));

        public Task<IReadOnlyList<RecurringSeries>> ListByOwnerAsync(string ownerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RecurringSeries>>(Items.Values.Where(s => s.OwnerId == ownerId).ToList());

        public Task AddAsync(RecurringSeries series, CancellationToken ct)
        {
            Items[series.Id] = series;
            _revisions[series.Id] = series.Revision;
            return Task.CompletedTask;
        }

        public Task<bool> TryUpdateAsync(RecurringSeries series, int expectedRevision, CancellationToken ct)
        {
            if (!_revisions.TryGetValue(series.Id, out var stored) || stored != expectedRevision)
                return Task.FromResult(false);
            Items[series.Id] = series;
            _revisions[series.Id] = series.Revision;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<RecurringSeries>> ListActiveByOwnerAsync(string ownerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RecurringSeries>>(Items.Values
                .Where(s => s.OwnerId == ownerId && s.Status == SeriesStatus.Active)
                .OrderBy(s => s.CreatedAtUtc).ThenBy(s => s.Id).ToList());

        public Task<IReadOnlyList<RecurringSeries>> ListDueAsync(DateTime nowUtc, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RecurringSeries>>(Items.Values
                .Where(s => s.Status == SeriesStatus.Active && s.NextOccurrenceUtc <= nowUtc)
                .OrderBy(s => s.NextOccurrenceUtc).Take(limit).ToList());
    }

    internal sealed class FakeOccurrences : ISeriesOccurrenceStore
    {
        public Dictionary<string, ScheduledPost> Items { get; } = new();
        private readonly HashSet<string> _reported = new();

        public Task<bool> TryAddAsync(ScheduledPost occurrence, CancellationToken ct) =>
            Task.FromResult(Items.TryAdd(occurrence.Id, occurrence));

        private static bool Unstarted(ScheduledPost p) =>
            p.Status == PublishStatus.Scheduled && p.LeaseHolder is null
            && p.Targets.All(t => t.AttemptCount == 0 && t.LastAttemptAtUtc is null);

        public Task<int> CancelUnstartedAsync(string seriesId, DateTime nowUtc, CancellationToken ct)
        {
            var hit = Items.Values.Where(p => p.SeriesId == seriesId && Unstarted(p)).ToList();
            foreach (var p in hit) { p.Cancel(); _reported.Add(p.Id); }
            return Task.FromResult(hit.Count);
        }

        public Task<int> SkipStaleAsync(DateTime cutoffUtc, DateTime nowUtc, CancellationToken ct)
        {
            var hit = Items.Values.Where(p => p.SeriesId is not null && p.ScheduledForUtc is { } due && due < cutoffUtc && Unstarted(p)).ToList();
            foreach (var p in hit) { p.Cancel(); _reported.Add(p.Id); }
            return Task.FromResult(hit.Count);
        }

        public Task<IReadOnlyList<ScheduledPost>> ListForSeriesAsync(string seriesId, int take, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ScheduledPost>>(Items.Values
                .Where(p => p.SeriesId == seriesId).OrderByDescending(p => p.OccurrenceAtUtc).Take(take).ToList());

        public Task<ScheduledPost?> ClaimNextUnreportedAsync(DateTime sinceUtc, DateTime nowUtc, CancellationToken ct)
        {
            var next = Items.Values
                .Where(p => p.SeriesId is not null && !_reported.Contains(p.Id)
                            && p.Status is PublishStatus.Published or PublishStatus.PartiallyPublished or PublishStatus.Failed
                            && p.CompletedAtUtc >= sinceUtc)
                .OrderBy(p => p.CompletedAtUtc).FirstOrDefault();
            if (next is not null) _reported.Add(next.Id);
            return Task.FromResult(next);
        }
    }

    /// <summary>The publish worker's view of the same occurrences.</summary>
    private sealed class Posts(FakeOccurrences store) : IScheduledPostRepository
    {
        public Task<bool> TryUpdateClaimedAsync(ScheduledPost post, string leaseHolder, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> TryUpdateIfStatusAsync(ScheduledPost post, PublishStatus expectedStatus, CancellationToken ct) => Task.FromResult(true);
        public Task<ScheduledPost?> FindByIdAsync(string id, CancellationToken ct) => Task.FromResult(store.Items.GetValueOrDefault(id));
        public Task AddAsync(ScheduledPost post, CancellationToken ct) { store.Items[post.Id] = post; return Task.CompletedTask; }
        public Task UpdateAsync(ScheduledPost post, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ScheduledPost>> ListAsync(ScheduledPostQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> CountAsync(ScheduledPostQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<ScheduledPost?> ClaimNextDueAsync(string leaseHolder, TimeSpan leaseDuration, DateTime nowUtc, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountPendingForConnectionAsync(string connectionId, CancellationToken ct) => Task.FromResult(0);
    }

    internal sealed class FakeConnections : ISocialConnectionRepository
    {
        public Task RenewCredentialAsync(string id, EncryptedSecret credential, DateTime? expiresAtUtc, CancellationToken ct) =>
            Task.CompletedTask;

        public Dictionary<string, SocialConnection> Items { get; } = new();

        public Task<SocialConnection?> FindByIdAsync(string id, CancellationToken ct) => Task.FromResult(Items.GetValueOrDefault(id));
        public Task UpdateAsync(SocialConnection connection, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<SocialConnection>> ListByOwnerAsync(string ownerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SocialConnection>>(Items.Values.Where(c => c.OwnerId == ownerId).ToList());
        public Task<SocialConnection?> FindByExternalAsync(string ownerId, SocialPlatform platform, string externalAccountId, CancellationToken ct) =>
            Task.FromResult(Items.Values.FirstOrDefault(c => c.OwnerId == ownerId && c.Platform == platform && c.ExternalAccountId == externalAccountId));
        public Task AddAsync(SocialConnection connection, CancellationToken ct) { Items[connection.Id] = connection; return Task.CompletedTask; }
        public Task<int> CountActiveAsync(string ownerId, CancellationToken ct) => Task.FromResult(Items.Count);
        public Task<IReadOnlyList<SocialConnection>> ListByAuthorizerAsync(IReadOnlyCollection<SocialPlatform> platforms, string userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SocialConnection>>(Array.Empty<SocialConnection>());
        public Task DeleteAsync(string id, CancellationToken ct) { Items.Remove(id); return Task.CompletedTask; }
        public Task<bool> GrantInUseAsync(string fingerprint, string exceptId, CancellationToken ct) => Task.FromResult(false);
        public Task<SocialConnection?> ClaimForHealthCheckAsync(IReadOnlyCollection<SocialPlatform> platforms, DateTime checkedBeforeUtc, DateTime nowUtc, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task RecordHealthAsync(string id, bool healthy, string? error, DateTime nowUtc, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    internal sealed class FakePublisher(SocialPlatform platform) : ISocialPublisher
    {
        private readonly Queue<PublishOutcome> _outcomes = new();
        public SocialPlatform Platform => platform;
        public bool IsConfigured => true;
        public void Next(PublishOutcome outcome) => _outcomes.Enqueue(outcome);
        public Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct) =>
            Task.FromResult(_outcomes.Count > 0 ? _outcomes.Dequeue() : new PublishOutcome(false, null, null, "No outcome queued."));
    }

    private sealed class Media : IPublishMediaResolver
    {
        public Task<ResolvedMedia?> ResolveAsync(
            PublishSourceKind kind, string sourceId, string ownerId, bool forInstagram, CancellationToken ct) =>
            Task.FromResult<ResolvedMedia?>(new ResolvedMedia(
                "https://blob/poster.png", IsVideo: false, "Flu clinic",
                forInstagram ? "https://blob/poster-ig.jpg" : null));
    }

    private sealed class PlainCipher : ITokenCipher
    {
        public bool IsConfigured => true;
        public EncryptedSecret Encrypt(string plaintext) => EncryptedSecret.Create(plaintext, "n", "t", 1);
        public string Decrypt(EncryptedSecret secret) => secret.CipherText;
        public bool CanDecrypt(EncryptedSecret secret) => true;
    }
}
