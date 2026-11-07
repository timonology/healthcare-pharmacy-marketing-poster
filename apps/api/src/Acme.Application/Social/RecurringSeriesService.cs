using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Application.Posters;
using Acme.Application.Subscriptions;
using Acme.Application.Videos;
using Acme.Domain.Common;
using Acme.Domain.Social;
using Acme.Domain.Subscriptions;
using Acme.Domain.Videos;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Social;

/// <summary>
/// Recurring social posts: what the pharmacy sets up, and what the scheduler
/// does when an occurrence falls due.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here publishes.</b> A due occurrence becomes an ordinary
/// <see cref="ScheduledPost"/> with a stable id, and the existing publish
/// worker sends it — same lease, same retries, same Uncertain handling, same
/// "this account needs reconnecting" failure for one target while the others
/// go out. One-off publishing is untouched.
/// </para>
/// <para>
/// <b>Checked on every occurrence, not only at setup</b>: an account that has
/// been deactivated or dropped to Free, a poster taken back to draft, a video
/// deleted, or more active series than the plan now allows. Any of those stops
/// that post and pauses the series, with the reason in words, and the pharmacy
/// is emailed once.
/// </para>
/// </remarks>
public sealed class RecurringSeriesService(
    IRecurringSeriesRepository seriesRepo,
    ISeriesOccurrenceStore occurrences,
    ISocialConnectionRepository connections,
    IUserRepository users,
    IPosterRepository posters,
    IVideoRepository videos,
    IPublishMediaResolver media,
    ISeriesNotifier notifier,
    ILogger<RecurringSeriesService> log)
{
    /// <summary>The calendar strip on the Recurring tab.</summary>
    public const int CalendarDays = 14;

    private const string FreePlan =
        "Recurring posts are on the Starter and Pro plans. Upgrade to set one up.";

    private const string MovedOn = "This series changed a moment ago. Refresh and try again.";

    /* ---------------------------------------------------------------- *
     * Pharmacy-facing
     * ---------------------------------------------------------------- */

    public async Task<Result<RecurringSeriesListDto>> ListAsync(string ownerId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null) return Result<RecurringSeriesListDto>.NotFound("Account not found.");

        var plan = Plans.For(user.Tier);
        var all = await seriesRepo.ListByOwnerAsync(ownerId, ct);
        var owned = await connections.ListByOwnerAsync(ownerId, ct);
        var now = DateTime.UtcNow;

        var items = new List<RecurringSeriesDto>(all.Count);
        foreach (var s in all)
            items.Add(await ToDtoAsync(s, owned, now, recent: 5, ct));

        return Result<RecurringSeriesListDto>.Success(new RecurringSeriesListDto(
            items,
            all.Count(s => s.Status == SeriesStatus.Active),
            plan.MaxRecurringSeries,
            plan.MaxRecurringSeries == 0 ? FreePlan : null));
    }

    public async Task<Result<RecurringSeriesDto>> GetAsync(string ownerId, string id, CancellationToken ct)
    {
        var s = await seriesRepo.FindByIdAsync(id, ct);
        if (s is null || s.OwnerId != ownerId) return Result<RecurringSeriesDto>.NotFound("Series not found.");
        var owned = await connections.ListByOwnerAsync(ownerId, ct);
        return Result<RecurringSeriesDto>.Success(await ToDtoAsync(s, owned, DateTime.UtcNow, recent: 20, ct));
    }

    /// <summary>The dates a rule would post on, for the When and Review steps.</summary>
    /// <remarks>Answers even for an unusable rule, with the problem in words, so the form can say what is wrong as they type.</remarks>
    public Result<SeriesPreviewDto> Preview(SeriesPreviewRequest request, DateTime nowUtc)
    {
        var zoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? "Europe/London" : request.TimeZoneId.Trim();
        var zone = RecurringSeries.ResolveZone(zoneId);
        if (zone is null)
            return Result<SeriesPreviewDto>.Success(new SeriesPreviewDto(
                Array.Empty<SeriesTimeDto>(), "", zoneId, "That time zone is not recognised."));

        var (cadence, cadenceProblem) = ReadCadence(request.Cadence);
        var start = SeriesWording.ParseDay(request.StartDate);
        var end = string.IsNullOrWhiteSpace(request.EndDate) ? null : SeriesWording.ParseDay(request.EndDate);

        string? problem = cadenceProblem
            ?? (start is null ? "Choose a start date." : null)
            ?? (!string.IsNullOrWhiteSpace(request.EndDate) && end is null ? "That end date is not a date." : null)
            ?? (end is { } e && start is { } st && e < st ? "The end date must be on or after the start date." : null);

        if (problem is not null || cadence is null || start is null)
            return Result<SeriesPreviewDto>.Success(new SeriesPreviewDto(
                Array.Empty<SeriesTimeDto>(), cadence is null ? "" : SeriesWording.Describe(cadence),
                SeriesWording.ZoneLabel(zoneId), problem));

        var count = Math.Clamp(request.Count ?? 3, 1, 31);
        var next = CadenceSchedule.Upcoming(cadence, zone, start.Value, end, nowUtc, count)
            .Select(t => SeriesWording.Time(t, zone, zoneId))
            .ToList();

        return Result<SeriesPreviewDto>.Success(new SeriesPreviewDto(
            next,
            SeriesWording.Describe(cadence),
            SeriesWording.ZoneLabel(zoneId),
            next.Count == 0 ? "With these settings there is nothing left to post." : null));
    }

    public async Task<Result<RecurringSeriesDto>> CreateAsync(
        string ownerId, SeriesRequest request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null) return Result<RecurringSeriesDto>.NotFound("Account not found.");

        var plan = Plans.For(user.Tier);
        if (plan.MaxRecurringSeries == 0) return Result<RecurringSeriesDto>.Forbidden(FreePlan);

        if (request is null || string.IsNullOrWhiteSpace(request.SourceId))
            return Result<RecurringSeriesDto>.Invalid("Choose a poster or video.");

        // Enforced here, not only in the form: the plan is what is sold.
        var active = await seriesRepo.ListActiveByOwnerAsync(ownerId, ct);
        if (active.Count >= plan.MaxRecurringSeries)
            return Result<RecurringSeriesDto>.Forbidden(LimitReached(plan));

        if (request.SourceKind == PublishSourceKind.Poster)
        {
            var poster = await posters.FindByIdAsync(request.SourceId, ct);
            if (poster is null || poster.OwnerId != ownerId)
                return Result<RecurringSeriesDto>.NotFound("That poster no longer exists.");
            if (PosterUsage.WhyCannotSend(poster, "posted to social") is { } cannotPost)
                return Result<RecurringSeriesDto>.Invalid(cannotPost);
        }

        var resolved = await media.ResolveAsync(request.SourceKind, request.SourceId, ownerId, forInstagram: false, ct);
        if (resolved is null)
            return Result<RecurringSeriesDto>.NotFound(request.SourceKind == PublishSourceKind.Poster
                ? "That poster no longer exists."
                : "That video no longer exists, or it has not finished rendering.");

        var form = await ReadFormAsync(ownerId, request, resolved.IsVideo, existing: null, ct);
        if (form.Error is { } formError) return formError;

        RecurringSeries series;
        try
        {
            series = RecurringSeries.Create(
                ownerId, request.Name ?? resolved.Name, request.SourceKind, request.SourceId, resolved.Name,
                request.Caption ?? string.Empty, form.Destinations!, form.Cadence!, form.ZoneId!,
                form.Start!.Value, form.End, request.Notify ?? SeriesNotify.All, now);
        }
        catch (DomainException ex)
        {
            return Result<RecurringSeriesDto>.Invalid(ex.Message);
        }

        await seriesRepo.AddAsync(series, ct);

        log.LogInformation(
            "Pharmacy {Owner} created series {Series} ({Cadence}) to {Count} account(s), first {Next}",
            ownerId, series.Id, SeriesWording.Describe(series.Cadence), series.Destinations.Count,
            series.NextOccurrenceUtc?.ToString("O"));

        // "Also post now": one occurrence at this minute, through the same
        // path as every other, so it gets the same id rules and checks.
        if (request.PostNow)
        {
            var at = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
            await occurrences.TryAddAsync(
                ScheduledPost.CreateOccurrence(series, at, series.Destinations.Select(d => (d.ConnectionId, d.Platform))),
                ct);
        }

        var owned = await connections.ListByOwnerAsync(ownerId, ct);
        return Result<RecurringSeriesDto>.Success(await ToDtoAsync(series, owned, now, recent: 5, ct));
    }

    /// <summary>Changes name, caption, accounts or schedule. Future occurrences only.</summary>
    public async Task<Result<RecurringSeriesDto>> UpdateAsync(
        string ownerId, string id, SeriesRequest request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var series = await seriesRepo.FindByIdAsync(id, ct);
        if (series is null || series.OwnerId != ownerId) return Result<RecurringSeriesDto>.NotFound("Series not found.");

        var isVideo = series.SourceKind == PublishSourceKind.Video;
        var form = await ReadFormAsync(
            ownerId, request, isVideo, series.Destinations.Select(d => d.ConnectionId).ToHashSet(), ct);
        if (form.Error is { } formError) return formError;

        var before = series.Revision;
        try
        {
            series.Edit(
                request.Name ?? series.Name, request.Caption ?? string.Empty, form.Destinations!, form.Cadence!,
                form.ZoneId!, form.Start!.Value, form.End, request.Notify ?? series.Notify, now);
        }
        catch (DomainException ex)
        {
            return Result<RecurringSeriesDto>.Invalid(ex.Message);
        }

        if (!await seriesRepo.TryUpdateAsync(series, before, ct))
            return Result<RecurringSeriesDto>.Conflict(MovedOn);

        // Anything queued but not yet started was made with the old settings.
        // Posts already sent, or being sent, stay exactly as they are.
        await occurrences.CancelUnstartedAsync(series.Id, now, ct);

        var owned = await connections.ListByOwnerAsync(ownerId, ct);
        return Result<RecurringSeriesDto>.Success(await ToDtoAsync(series, owned, now, recent: 5, ct));
    }

    public Task<Result<RecurringSeriesDto>> PauseAsync(string ownerId, string id, CancellationToken ct) =>
        ChangeAsync(ownerId, id, ct, (s, _) => { s.Pause(); return Task.FromResult<Result<RecurringSeriesDto>?>(null); });

    public Task<Result<RecurringSeriesDto>> EndAsync(string ownerId, string id, CancellationToken ct) =>
        ChangeAsync(ownerId, id, ct, (s, _) => { s.End(); return Task.FromResult<Result<RecurringSeriesDto>?>(null); });

    /// <summary>Back on, from now. Checked against the plan like a new series.</summary>
    public Task<Result<RecurringSeriesDto>> ResumeAsync(string ownerId, string id, CancellationToken ct) =>
        ChangeAsync(ownerId, id, ct, async (s, now) =>
        {
            if (s.Status == SeriesStatus.Active) return null;

            var user = await users.FindByIdAsync(ownerId, ct);
            var plan = Plans.For(user?.Tier ?? SubscriptionTier.Free);
            if (plan.MaxRecurringSeries == 0) return Result<RecurringSeriesDto>.Forbidden(FreePlan);

            var active = await seriesRepo.ListActiveByOwnerAsync(ownerId, ct);
            if (active.Count >= plan.MaxRecurringSeries)
                return Result<RecurringSeriesDto>.Forbidden(LimitReached(plan));

            if (await WhyNotAsync(s, now, ignoreLimit: true, ct) is { } reason)
                return Result<RecurringSeriesDto>.Invalid(reason);

            s.Resume(now);
            return null;
        });

    /// <summary>Load, change, save conditionally; on pause or end, cancel what is queued but unstarted.</summary>
    private async Task<Result<RecurringSeriesDto>> ChangeAsync(
        string ownerId, string id, CancellationToken ct,
        Func<RecurringSeries, DateTime, Task<Result<RecurringSeriesDto>?>> change)
    {
        var now = DateTime.UtcNow;
        var series = await seriesRepo.FindByIdAsync(id, ct);
        if (series is null || series.OwnerId != ownerId) return Result<RecurringSeriesDto>.NotFound("Series not found.");

        var before = series.Revision;
        try
        {
            if (await change(series, now) is { } refused) return refused;
        }
        catch (DomainException ex)
        {
            return Result<RecurringSeriesDto>.Invalid(ex.Message);
        }

        if (series.Revision != before && !await seriesRepo.TryUpdateAsync(series, before, ct))
            return Result<RecurringSeriesDto>.Conflict(MovedOn);

        // Pause and End stop future posts. Already sent (or sending) ones are
        // history and stay.
        if (series.Status != SeriesStatus.Active)
            await occurrences.CancelUnstartedAsync(series.Id, now, ct);

        var owned = await connections.ListByOwnerAsync(ownerId, ct);
        return Result<RecurringSeriesDto>.Success(await ToDtoAsync(series, owned, now, recent: 5, ct));
    }

    /* ---------------------------------------------------------------- *
     * Scheduler-facing
     * ---------------------------------------------------------------- */

    /// <summary>
    /// One pass of the scheduler: skip anything queued and now too late, then
    /// turn every due series' next occurrence into a post.
    /// </summary>
    /// <returns>How many series were looked at.</returns>
    public async Task<int> RunDueAsync(DateTime nowUtc, CancellationToken ct)
    {
        var stale = await occurrences.SkipStaleAsync(nowUtc - RecurringSeries.LateLimit, nowUtc, ct);
        if (stale > 0)
            log.LogWarning("Skipped {Count} recurring occurrence(s) queued but never sent, now over two hours late", stale);

        var due = await seriesRepo.ListDueAsync(nowUtc, 50, ct);
        foreach (var series in due)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await ProcessDueAsync(series, nowUtc, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad series must not stop the others.
                log.LogError(ex, "Recurring series {Series} could not be processed; will try again", series.Id);
            }
        }
        return due.Count;
    }

    /// <summary>Handles one due series. Public for tests; the scheduler calls <see cref="RunDueAsync"/>.</summary>
    public async Task ProcessDueAsync(RecurringSeries series, DateTime nowUtc, CancellationToken ct)
    {
        var before = series.Revision;
        var plan = series.PlanDue(nowUtc);

        // Not due after all (paused, or moved on, since it was listed).
        if (plan.PostAtUtc is null && plan.Missed == 0) return;

        if (plan.PostAtUtc is { } at)
        {
            // The plan, the account and the source, every time.
            if (await WhyNotAsync(series, nowUtc, ignoreLimit: false, ct) is { } reason)
            {
                series.Pause(reason, bySystem: true);
                if (await seriesRepo.TryUpdateAsync(series, before, ct))
                {
                    await occurrences.CancelUnstartedAsync(series.Id, nowUtc, ct);
                    log.LogInformation("Series {Series} paused: {Reason}", series.Id, reason);
                    await notifier.SeriesPausedAsync(series, reason, ct);
                }
                return;
            }

            // Post first, then move the series on. A crash between the two
            // leaves the series due again with the same occurrence, which
            // makes the same id, which the database refuses — so it is posted
            // once. The other order would lose the post instead.
            // Accounts removed since setup are left off: a post to an account
            // that no longer exists can only fail, every time. One that needs
            // reconnecting stays on and fails for this post alone, so the
            // pharmacy is told and can fix it.
            var live = await LiveDestinationsAsync(series, ct);
            var occurrence = ScheduledPost.CreateOccurrence(
                series, at, live.Select(d => (d.ConnectionId, d.Platform)));
            var created = await occurrences.TryAddAsync(occurrence, ct);

            log.LogInformation(
                created
                    ? "Series {Series} queued occurrence {Post} for {At}"
                    : "Series {Series} occurrence {Post} for {At} was already queued",
                series.Id, occurrence.Id, at.ToString("O"));
        }

        if (plan.Missed > 0)
            log.LogWarning(
                "Series {Series} skipped {Missed} occurrence(s) more than two hours late", series.Id, plan.Missed);

        series.Advance(plan, nowUtc);
        if (await seriesRepo.TryUpdateAsync(series, before, ct)) return;

        // Someone changed it underneath us — most likely the pharmacy pausing
        // or editing as it fell due. If it is no longer active, the post we may
        // just have queued must not go out: their pause cancelled what existed
        // when they pressed it, which may have been before ours was written.
        var fresh = await seriesRepo.FindByIdAsync(series.Id, ct);
        if (fresh is null || fresh.Status != SeriesStatus.Active)
            await occurrences.CancelUnstartedAsync(series.Id, nowUtc, ct);
    }

    /// <summary>Emails the outcome of finished occurrences, each once.</summary>
    /// <returns>How many were reported.</returns>
    public async Task<int> ReportOutcomesAsync(DateTime nowUtc, CancellationToken ct)
    {
        const int MaxPerPass = 25;
        var reported = 0;

        for (; reported < MaxPerPass && !ct.IsCancellationRequested; reported++)
        {
            // A day back at most, so a deployment does not email old history.
            var post = await occurrences.ClaimNextUnreportedAsync(nowUtc.AddDays(-1), nowUtc, ct);
            if (post is null) break;

            var series = post.SeriesId is { } sid ? await seriesRepo.FindByIdAsync(sid, ct) : null;
            if (series is null) continue;

            await notifier.OccurrenceFinishedAsync(series, post, ct);
        }

        return reported;
    }

    /* ---------------------------------------------------------------- *
     * Rules
     * ---------------------------------------------------------------- */

    /// <summary>Why this series may not post now, in the pharmacy's words, or null.</summary>
    private async Task<string?> WhyNotAsync(RecurringSeries series, DateTime nowUtc, bool ignoreLimit, CancellationToken ct)
    {
        var owner = await users.FindByIdAsync(series.OwnerId, ct);
        if (owner is null || !owner.IsActive)
            return "The account this series belongs to is no longer active.";

        var plan = Plans.For(owner.Tier);
        if (plan.MaxRecurringSeries == 0)
            return "Recurring posts are on the Starter and Pro plans, and this account is now on Free.";

        if (!ignoreLimit)
        {
            // After a downgrade there can be more active series than the plan
            // allows. The oldest keep running; the rest pause, one at a time,
            // as each falls due.
            var active = await seriesRepo.ListActiveByOwnerAsync(series.OwnerId, ct);
            var place = active.ToList().FindIndex(s => s.Id == series.Id);
            if (place >= plan.MaxRecurringSeries)
                return $"The {plan.DisplayName} plan allows {plan.MaxRecurringSeries} active recurring " +
                       $"post{(plan.MaxRecurringSeries == 1 ? "" : "s")}, so this one was paused.";
        }

        if ((await LiveDestinationsAsync(series, ct)).Count == 0)
            return "None of the accounts this series posts to are connected any more. " +
                   "Edit it to choose another, or connect the account again.";

        if (series.SourceKind == PublishSourceKind.Poster)
        {
            var poster = await posters.FindByIdAsync(series.SourceId, ct);
            if (poster is null || poster.OwnerId != series.OwnerId)
                return "The poster for this series has been deleted.";
            if (PosterUsage.WhyCannotSend(poster, "posted to social") is { } cannotPost)
                return cannotPost;
        }
        else
        {
            var video = await videos.FindByIdAsync(series.SourceId, ct);
            if (video is null || video.OwnerId != series.OwnerId)
                return "The video for this series has been deleted.";
            if (video.Status != VideoStatus.Ready)
                return "The video for this series is not ready to post.";
        }

        return null;
    }

    /// <summary>The series' accounts that still exist and are not revoked (reconnect-needed included).</summary>
    private async Task<IReadOnlyList<SeriesDestination>> LiveDestinationsAsync(RecurringSeries series, CancellationToken ct)
    {
        var owned = await connections.ListByOwnerAsync(series.OwnerId, ct);
        return series.Destinations
            .Where(d => owned.Any(c => c.Id == d.ConnectionId && c.Status != ConnectionStatus.Revoked))
            .ToList();
    }

    private static string LimitReached(PlanDefinition plan) =>
        $"The {plan.DisplayName} plan allows {plan.MaxRecurringSeries} active recurring " +
        $"post{(plan.MaxRecurringSeries == 1 ? "" : "s")}. Pause or end one first" +
        (plan.Tier == SubscriptionTier.Starter ? ", or upgrade to Pro for up to 20." : ".");

    private sealed record Form(
        Result<RecurringSeriesDto>? Error,
        IReadOnlyList<SeriesDestination>? Destinations = null,
        Cadence? Cadence = null,
        string? ZoneId = null,
        DateOnly? Start = null,
        DateOnly? End = null);

    /// <summary>Reads and checks the parts of a request that create and edit share.</summary>
    /// <remarks>
    /// The account rules are the one-off publish rules, plus: Facebook,
    /// Instagram, YouTube (videos) and LinkedIn only for now — not Google
    /// Business Profile.
    /// </remarks>
    /// <param name="existing">
    /// On edit, the accounts already on the series. One of those that now
    /// needs reconnecting may stay: its posts fail one at a time, as they
    /// would have anyway, and refusing it would stop the pharmacy changing
    /// anything else until they reconnect. A newly added account must work.
    /// </param>
    private async Task<Form> ReadFormAsync(
        string ownerId, SeriesRequest request, bool isVideo, IReadOnlySet<string>? existing, CancellationToken ct)
    {
        static Form Bad(string message) => new(Result<RecurringSeriesDto>.Invalid(message));

        if (request.ConnectionIds is not { Count: > 0 })
            return Bad("Choose at least one account to post to.");

        var owned = await connections.ListByOwnerAsync(ownerId, ct);
        var chosen = new List<SeriesDestination>();
        foreach (var connectionId in request.ConnectionIds.Distinct())
        {
            var connection = owned.FirstOrDefault(c => c.Id == connectionId);

            // Not found and not-yours are the same answer, as for one-off posts.
            if (connection is null || connection.Status == ConnectionStatus.Revoked)
                return new Form(Result<RecurringSeriesDto>.NotFound("One of the chosen accounts is no longer connected."));

            // TikTok's sharing rules want the pharmacy to choose each post's
            // audience and settings, and approve it — not a schedule to post
            // on its own.
            if (connection.Platform == SocialPlatform.TikTokAccount)
                return Bad("TikTok posts are chosen one at a time (TikTok asks you to pick each post's audience), so TikTok cannot be part of a recurring post.");

            // X refuses a post whose text repeats one already on the account,
            // and a series repeats its caption every time: every occurrence
            // after the first would fail.
            if (connection.Platform == SocialPlatform.XAccount)
                return Bad("X does not allow the same text to be posted again, and a recurring post repeats its caption each time, so X cannot be part of a recurring post. Share to X one post at a time.");

            if (connection.Platform is not (SocialPlatform.FacebookPage or SocialPlatform.InstagramBusiness
                    or SocialPlatform.YouTubeChannel or SocialPlatform.LinkedInProfile or SocialPlatform.LinkedInPage))
                return Bad($"Recurring posts can go to Facebook, Instagram, YouTube and LinkedIn. {connection.Platform.DisplayName()} is not supported yet.");

            if (!isVideo && !connection.Platform.SupportsImages())
                return Bad($"{connection.Platform.DisplayName()} only takes videos. Choose a video for this series, or remove it.");

            if (connection.Status == ConnectionStatus.NeedsReauth && existing?.Contains(connection.Id) != true)
                return Bad($"{connection.DisplayName} needs to be reconnected before it can be posted to.");

            if (isVideo && !connection.Platform.SupportsVideo())
                return Bad($"{connection.Platform.DisplayName()} cannot publish videos.");

            chosen.Add(new SeriesDestination(connection.Id, connection.Platform));
        }

        var (cadence, cadenceProblem) = ReadCadence(request.Cadence);
        if (cadenceProblem is not null) return Bad(cadenceProblem);

        var zoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? "Europe/London" : request.TimeZoneId.Trim();
        if (RecurringSeries.ResolveZone(zoneId) is null) return Bad("That time zone is not recognised.");

        var start = SeriesWording.ParseDay(request.StartDate);
        if (start is null) return Bad("Choose a start date.");

        DateOnly? end = null;
        if (!string.IsNullOrWhiteSpace(request.EndDate))
        {
            end = SeriesWording.ParseDay(request.EndDate);
            if (end is null) return Bad("That end date is not a date.");
        }

        return new Form(null, chosen, cadence, zoneId, start, end);
    }

    private static (Cadence? Cadence, string? Problem) ReadCadence(CadenceRequest? r)
    {
        if (r is null) return (null, "Choose how often to post.");
        if (!Enum.IsDefined(r.Kind)) return (null, "Choose how often to post.");
        if (SeriesWording.ParseTime(r.Time) is not { } time) return (null, "Choose a time of day.");

        var dates = new List<DateOnly>();
        foreach (var raw in r.Dates ?? Array.Empty<string>())
        {
            if (SeriesWording.ParseDay(raw) is not { } d) return (null, $"\"{raw}\" is not a date.");
            dates.Add(d);
        }

        var days = (r.Days ?? Array.Empty<DayOfWeek>()).Where(d => Enum.IsDefined(d)).ToList();
        var cadence = new Cadence(r.Kind, time, days, r.IntervalDays ?? 1, dates).Normalised();
        return (cadence, cadence.Problem());
    }

    /* ---------------------------------------------------------------- *
     * Mapping
     * ---------------------------------------------------------------- */

    private async Task<RecurringSeriesDto> ToDtoAsync(
        RecurringSeries s, IReadOnlyList<SocialConnection> owned, DateTime nowUtc, int recent, CancellationToken ct)
    {
        var zone = s.Zone();

        var destinations = s.Destinations.Select(d =>
        {
            var c = owned.FirstOrDefault(x => x.Id == d.ConnectionId);
            var gone = c is null || c.Status == ConnectionStatus.Revoked;
            return new SeriesDestinationDto(
                d.ConnectionId,
                d.Platform,
                d.Platform.DisplayName(),
                c?.DisplayName ?? "(disconnected account)",
                c?.Status,
                NeedsReconnect: gone || c!.Status == ConnectionStatus.NeedsReauth);
        }).ToList();

        // The calendar: occurrences in the next 14 days, active series only.
        var upcoming = s.Status == SeriesStatus.Active && s.NextOccurrenceUtc is { } next
            ? new[] { next }
                .Concat(s.Upcoming(next, CalendarDays + 1))
                .Where(t => t <= nowUtc.AddDays(CalendarDays))
                .Select(t => SeriesWording.Time(t, zone, s.TimeZoneId))
                .ToList()
            : new List<SeriesTimeDto>();

        var history = await occurrences.ListForSeriesAsync(s.Id, recent, ct);
        var recentDtos = history.Select(p => new SeriesOccurrenceDto(
            p.Id,
            p.OccurrenceAtUtc ?? p.ScheduledForUtc ?? p.CreatedAtUtc,
            SeriesWording.Time(p.OccurrenceAtUtc ?? p.ScheduledForUtc ?? p.CreatedAtUtc, zone, s.TimeZoneId).Label,
            p.Status,
            p.Targets.Select(t =>
            {
                var c = owned.FirstOrDefault(x => x.Id == t.ConnectionId);
                return new PublishTargetDto(
                    t.ConnectionId, t.Platform, t.Platform.DisplayName(),
                    c?.DisplayName ?? "(disconnected account)", t.Status, t.ExternalPostId,
                    t.Permalink, t.Error, t.AttemptCount, t.PublishedAtUtc);
            }).ToList())).ToList();

        return new RecurringSeriesDto(
            s.Id,
            s.Name,
            s.SourceKind,
            s.SourceId,
            s.SourceName,
            s.Caption,
            destinations,
            s.Cadence.Kind,
            s.Cadence.TimeOfDay.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            (s.Cadence.Days ?? Array.Empty<DayOfWeek>()).ToList(),
            s.Cadence.IntervalDays,
            (s.Cadence.Dates ?? Array.Empty<DateOnly>())
                .Select(d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).ToList(),
            s.TimeZoneId,
            SeriesWording.ZoneLabel(s.TimeZoneId),
            s.StartDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            s.EndDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            s.Status,
            s.StatusReason,
            s.PausedBySystem,
            s.Notify,
            SeriesWording.Describe(s.Cadence),
            s.NextOccurrenceUtc,
            s.NextOccurrenceUtc is { } n ? SeriesWording.Time(n, zone, s.TimeZoneId).Label : null,
            upcoming,
            recentDtos,
            s.MissedCount,
            s.CreatedAtUtc);
    }
}
