using Acme.Domain.Common;

namespace Acme.Domain.Social;

/// <summary>Where a series is in its life. Persisted as numbers: never renumber.</summary>
public enum SeriesStatus
{
    Active = 0,
    Paused = 1,
    Ended = 2,
}

/// <summary>How often a series posts. Persisted as numbers: never renumber.</summary>
public enum CadenceKind
{
    /// <summary>Every day at the chosen time.</summary>
    Daily = 0,

    /// <summary>On the chosen weekdays at the chosen time.</summary>
    Weekly = 1,

    /// <summary>Every N days from the start date, at the chosen time.</summary>
    EveryNDays = 2,

    /// <summary>On a hand-picked list of dates, at the chosen time.</summary>
    SpecificDates = 3,
}

/// <summary>Which emails the pharmacy gets about a series.</summary>
public enum SeriesNotify
{
    /// <summary>Every post that goes out, every problem, and any pause.</summary>
    All = 0,

    /// <summary>Only when something needs them: a failure, a reconnect, a pause.</summary>
    ProblemsOnly = 1,

    /// <summary>Nothing by email; the Recurring tab still shows it all.</summary>
    Off = 2,
}

/// <summary>
/// When a series posts, in the pharmacy's own wall-clock terms.
/// </summary>
/// <remarks>
/// <para>
/// <b>Local time, not UTC.</b> "Every Monday at 9:00" means 9:00 on the
/// pharmacy's clock in March and in November alike, so the rule is kept as a
/// local time and turned into an instant only when an occurrence is worked
/// out — see <see cref="CadenceSchedule"/>. Storing a UTC instant and adding a
/// week would drift by an hour at every clock change.
/// </para>
/// </remarks>
public sealed record Cadence(
    CadenceKind Kind,
    TimeOnly TimeOfDay,
    IReadOnlyList<DayOfWeek>? Days = null,
    int IntervalDays = 1,
    IReadOnlyList<DateOnly>? Dates = null)
{
    public const int MaxIntervalDays = 365;
    public const int MaxSpecificDates = 60;

    /// <summary>The rule normalised: days sorted and distinct, dates sorted and distinct.</summary>
    public Cadence Normalised() => this with
    {
        Days = Kind == CadenceKind.Weekly
            ? (Days ?? Array.Empty<DayOfWeek>()).Distinct().OrderBy(d => ((int)d + 6) % 7).ToList()
            : Array.Empty<DayOfWeek>(),
        IntervalDays = Kind == CadenceKind.EveryNDays ? IntervalDays : 1,
        Dates = Kind == CadenceKind.SpecificDates
            ? (Dates ?? Array.Empty<DateOnly>()).Distinct().Order().ToList()
            : Array.Empty<DateOnly>(),
        // Minutes are what the picker offers; seconds would only ever be noise.
        TimeOfDay = new TimeOnly(TimeOfDay.Hour, TimeOfDay.Minute),
    };

    /// <summary>Why this rule cannot be used, or null.</summary>
    public string? Problem() => Kind switch
    {
        CadenceKind.Weekly when Days is not { Count: > 0 } => "Choose at least one day of the week.",
        CadenceKind.EveryNDays when IntervalDays is < 1 or > MaxIntervalDays =>
            $"Choose a gap between 1 and {MaxIntervalDays} days.",
        CadenceKind.SpecificDates when Dates is not { Count: > 0 } => "Choose at least one date.",
        CadenceKind.SpecificDates when Dates!.Count > MaxSpecificDates =>
            $"Choose up to {MaxSpecificDates} dates.",
        _ when !Enum.IsDefined(Kind) => "Choose how often to post.",
        _ => null,
    };

    /// <summary>True when a local calendar day is one this rule posts on.</summary>
    public bool Includes(DateOnly day, DateOnly startDate) => Kind switch
    {
        CadenceKind.Daily => true,
        CadenceKind.Weekly => Days?.Contains(day.DayOfWeek) == true,
        CadenceKind.EveryNDays =>
            day >= startDate && (day.DayNumber - startDate.DayNumber) % Math.Max(1, IntervalDays) == 0,
        CadenceKind.SpecificDates => Dates?.Contains(day) == true,
        _ => false,
    };
}

/// <summary>
/// Turns a cadence into instants, in a given time zone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Clock changes.</b> In Europe/London the clocks go forward at 01:00 on
/// the last Sunday of March (01:00–01:59 does not exist) and back at 02:00 on
/// the last Sunday of October (01:00–01:59 happens twice). A time in the gap
/// is moved forward by the hour the clocks skip — a 01:30 post goes out at
/// 02:30 BST, late rather than not at all. A time in the overlap uses the first of the
/// two, the summer-time one, so it goes out once and as early as the clock
/// first shows it.
/// </para>
/// <para>
/// Pure: nothing here reads the clock or the database, so it can be tested
/// with any "now" and any zone.
/// </para>
/// </remarks>
public static class CadenceSchedule
{
    /// <summary>How far ahead to look before deciding there is no next occurrence.</summary>
    /// <remarks>
    /// Longer than the longest gap a valid rule can have (365 days), so an
    /// every-365-days series still finds its next date.
    /// </remarks>
    public const int HorizonDays = 400;

    /// <summary>
    /// The first occurrence strictly after <paramref name="afterUtc"/>, or null
    /// if the series has none (past its end date, or out of specific dates).
    /// </summary>
    public static DateTime? NextAfter(
        Cadence cadence,
        TimeZoneInfo zone,
        DateOnly startDate,
        DateOnly? endDate,
        DateTime afterUtc)
    {
        afterUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);

        // Start a day early: "after" in UTC can still be the previous local
        // day, and the check below discards anything not strictly later.
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone);
        var day = DateOnly.FromDateTime(localNow).AddDays(-1);
        if (day < startDate) day = startDate;

        var last = DateOnly.FromDateTime(localNow).AddDays(HorizonDays);
        if (endDate is { } end && end < last) last = end;

        for (; day <= last; day = day.AddDays(1))
        {
            if (!cadence.Includes(day, startDate)) continue;
            var at = ToUtc(day, cadence.TimeOfDay, zone);
            if (at > afterUtc) return at;
        }

        return null;
    }

    /// <summary>The next <paramref name="count"/> occurrences after an instant.</summary>
    public static IReadOnlyList<DateTime> Upcoming(
        Cadence cadence, TimeZoneInfo zone, DateOnly startDate, DateOnly? endDate,
        DateTime afterUtc, int count)
    {
        var list = new List<DateTime>(Math.Max(0, count));
        var cursor = afterUtc;
        while (list.Count < count
               && NextAfter(cadence, zone, startDate, endDate, cursor) is { } next)
        {
            list.Add(next);
            cursor = next;
        }
        return list;
    }

    /// <summary>
    /// A local date and time as a UTC instant, resolving the clock-change gap
    /// and overlap as described on the class.
    /// </summary>
    public static DateTime ToUtc(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);

        // In the spring gap: move forward by the hour the clocks skip, so 01:30
        // becomes 02:30 — the same distance past the change as was asked for.
        // Zones with an odd-sized gap fall back to the first minute that exists.
        if (zone.IsInvalidTime(local))
        {
            var shifted = local.AddHours(1);
            if (!zone.IsInvalidTime(shifted))
            {
                local = shifted;
            }
            else
            {
                var guard = 0;
                while (zone.IsInvalidTime(local) && guard++ < 180)
                    local = local.AddMinutes(1);
            }
        }

        if (zone.IsAmbiguousTime(local))
        {
            // Both readings are real; the larger offset is the earlier instant.
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>A UTC instant as the pharmacy's local date and time.</summary>
    public static DateTime ToLocal(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
}

/// <summary>What the scheduler should do with a series that is due.</summary>
/// <param name="PostAtUtc">The one occurrence to post now, or null if none is fresh enough.</param>
/// <param name="Missed">Occurrences skipped because they were too late to send.</param>
/// <param name="NextUtc">Where the series moves on to; null when it has run out.</param>
public sealed record OccurrencePlan(DateTime? PostAtUtc, int Missed, DateTime? NextUtc);

/// <summary>
/// One poster or video, posted to the same accounts on a schedule until the
/// pharmacy pauses or ends it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A series never publishes anything itself.</b> When an occurrence is due
/// the scheduler turns it into an ordinary <see cref="ScheduledPost"/> with a
/// stable id — the series id plus the instant — and the existing publish
/// worker claims and sends that with the same lease, retries and Uncertain
/// handling as a one-off post. Two schedulers racing produce the same id, and
/// the database lets only one of them in, so an occurrence cannot be created
/// twice; the worker's claim means it cannot be sent twice.
/// </para>
/// <para>
/// <b>Never a burst.</b> If the scheduler was down, only the most recent
/// missed occurrence is posted, and only if it is under
/// <see cref="LateLimit"/> late. Everything older is skipped, so a pharmacy
/// whose server was off for a week does not wake up to seven identical posts.
/// </para>
/// </remarks>
public sealed class RecurringSeries : Entity
{
    /// <summary>How late an occurrence may be and still go out.</summary>
    public static readonly TimeSpan LateLimit = TimeSpan.FromHours(2);

    public const int NameMax = 80;
    public const int CaptionMax = 2200;

    /// <summary>The longest a series may be planned ahead: start no more than a year out.</summary>
    public const int MaxStartAheadDays = 366;

    public string OwnerId { get; private set; } = default!;
    public string Name { get; private set; } = string.Empty;

    public PublishSourceKind SourceKind { get; private set; }
    public string SourceId { get; private set; } = default!;
    public string SourceName { get; private set; } = string.Empty;
    public string Caption { get; private set; } = string.Empty;

    private readonly List<SeriesDestination> _destinations = new();
    public IReadOnlyList<SeriesDestination> Destinations => _destinations;

    public Cadence Cadence { get; private set; } = new(CadenceKind.Daily, new TimeOnly(9, 0));

    /// <summary>IANA id, e.g. <c>Europe/London</c>. The pharmacy's clock.</summary>
    public string TimeZoneId { get; private set; } = "Europe/London";

    public DateOnly StartDate { get; private set; }

    /// <summary>Last day that may have a post, inclusive. Null: until they stop it.</summary>
    public DateOnly? EndDate { get; private set; }

    public SeriesStatus Status { get; private set; } = SeriesStatus.Active;

    /// <summary>Why it is paused or ended, in words for the pharmacy. Null when they did it.</summary>
    public string? StatusReason { get; private set; }

    /// <summary>True when the system paused it (plan, source); false when the pharmacy did.</summary>
    public bool PausedBySystem { get; private set; }

    public SeriesNotify Notify { get; private set; } = SeriesNotify.All;

    /// <summary>The next occurrence still to be posted. Null when there is none, or the series is not active.</summary>
    public DateTime? NextOccurrenceUtc { get; private set; }

    /// <summary>The last occurrence handed to the publish worker.</summary>
    public DateTime? LastOccurrenceUtc { get; private set; }

    /// <summary>How many occurrences were skipped as too late, ever.</summary>
    public int MissedCount { get; private set; }

    public DateTime? LastMissedAtUtc { get; private set; }

    /// <summary>
    /// Bumped on every change. Saves are conditional on it, so the pharmacy
    /// pausing and the scheduler advancing at the same moment cannot quietly
    /// overwrite each other.
    /// </summary>
    public int Revision { get; private set; }

    private RecurringSeries() { }

    /// <summary>Creates a series. Active, with its first occurrence worked out from now.</summary>
    public static RecurringSeries Create(
        string ownerId,
        string name,
        PublishSourceKind sourceKind,
        string sourceId,
        string sourceName,
        string caption,
        IEnumerable<SeriesDestination> destinations,
        Cadence cadence,
        string timeZoneId,
        DateOnly startDate,
        DateOnly? endDate,
        SeriesNotify notify,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new DomainException("OwnerId is required.");
        if (string.IsNullOrWhiteSpace(sourceId)) throw new DomainException("Choose a poster or video.");

        var series = new RecurringSeries
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            SourceKind = sourceKind,
            SourceId = sourceId,
            SourceName = (sourceName ?? string.Empty).Trim(),
            Status = SeriesStatus.Active,
        };
        series.Apply(name, caption, destinations, cadence, timeZoneId, startDate, endDate, notify, nowUtc);
        return series;
    }

    /// <summary>
    /// Changes what, where or when. Future occurrences only: posts already
    /// handed to the worker are the service's to deal with, and history stays.
    /// </summary>
    public void Edit(
        string name, string caption, IEnumerable<SeriesDestination> destinations, Cadence cadence,
        string timeZoneId, DateOnly startDate, DateOnly? endDate, SeriesNotify notify, DateTime nowUtc)
    {
        if (Status == SeriesStatus.Ended)
            throw new DomainException("This series has ended. Start a new one instead.");
        Apply(name, caption, destinations, cadence, timeZoneId, startDate, endDate, notify, nowUtc);
    }

    private void Apply(
        string name, string caption, IEnumerable<SeriesDestination> destinations, Cadence cadence,
        string timeZoneId, DateOnly startDate, DateOnly? endDate, SeriesNotify notify, DateTime nowUtc)
    {
        var trimmedName = (name ?? string.Empty).Trim();
        if (trimmedName.Length == 0) trimmedName = SourceName.Length > 0 ? SourceName : "Recurring post";
        if (trimmedName.Length > NameMax) throw new DomainException($"Keep the name to {NameMax} characters.");

        var trimmedCaption = (caption ?? string.Empty).Trim();
        if (trimmedCaption.Length > CaptionMax)
            throw new DomainException($"Caption must be {CaptionMax} characters or fewer.");

        var list = (destinations ?? Array.Empty<SeriesDestination>())
            .DistinctBy(d => d.ConnectionId).ToList();
        if (list.Count == 0) throw new DomainException("Choose at least one account to post to.");

        var normalised = (cadence ?? throw new DomainException("Choose how often to post.")).Normalised();
        if (normalised.Problem() is { } problem) throw new DomainException(problem);

        var zone = ResolveZone(timeZoneId)
            ?? throw new DomainException("That time zone is not recognised.");

        if (endDate is { } end && end < startDate)
            throw new DomainException("The end date must be on or after the start date.");

        var today = DateOnly.FromDateTime(CadenceSchedule.ToLocal(nowUtc, zone));
        if (startDate > today.AddDays(MaxStartAheadDays))
            throw new DomainException("Start within the next year.");
        if (endDate is { } e2 && e2 < today)
            throw new DomainException("The end date has already passed.");

        Name = trimmedName;
        Caption = trimmedCaption;
        _destinations.Clear();
        _destinations.AddRange(list);
        Cadence = normalised;
        // As given (trimmed), not zone.Id: on Windows an IANA id can come back
        // under its Windows name, and the browser speaks IANA.
        TimeZoneId = timeZoneId!.Trim();
        StartDate = startDate;
        EndDate = endDate;
        Notify = notify;

        if (Status == SeriesStatus.Active)
        {
            NextOccurrenceUtc = NextFrom(nowUtc);
            if (NextOccurrenceUtc is null)
                throw new DomainException(
                    "With these settings there is nothing left to post. Check the dates and the end date.");
        }

        Changed();
    }

    /// <summary>The pharmacy pauses it. Nothing is deleted.</summary>
    public void Pause(string? reason = null, bool bySystem = false)
    {
        if (Status == SeriesStatus.Ended) throw new DomainException("This series has ended.");
        if (Status == SeriesStatus.Paused && !bySystem) return;

        Status = SeriesStatus.Paused;
        StatusReason = reason;
        PausedBySystem = bySystem;
        NextOccurrenceUtc = null;
        Changed();
    }

    /// <summary>
    /// Back on. The next occurrence is worked out from now, so nothing missed
    /// while paused is sent in a rush.
    /// </summary>
    public void Resume(DateTime nowUtc)
    {
        if (Status == SeriesStatus.Ended) throw new DomainException("This series has ended. Start a new one instead.");
        if (Status == SeriesStatus.Active) return;

        var next = NextFrom(nowUtc)
            ?? throw new DomainException("There is nothing left to post. Change the dates first.");

        Status = SeriesStatus.Active;
        StatusReason = null;
        PausedBySystem = false;
        NextOccurrenceUtc = next;
        Changed();
    }

    /// <summary>Finished for good. History stays; it cannot be resumed.</summary>
    public void End(string? reason = null)
    {
        if (Status == SeriesStatus.Ended) return;
        Status = SeriesStatus.Ended;
        StatusReason = reason;
        NextOccurrenceUtc = null;
        Changed();
    }

    /// <summary>
    /// What to do now the next occurrence is due: which one (if any) to post,
    /// how many to skip as too late, and where to move on to.
    /// </summary>
    /// <remarks>
    /// Walks forward through every occurrence up to now. Only the latest may
    /// be posted, and only if it is under <see cref="LateLimit"/> late; all
    /// earlier ones are skipped. That is the whole of the no-burst rule.
    /// </remarks>
    public OccurrencePlan PlanDue(DateTime nowUtc)
    {
        if (Status != SeriesStatus.Active || NextOccurrenceUtc is not { } due || due > nowUtc)
            return new OccurrencePlan(null, 0, NextOccurrenceUtc);

        var zone = Zone();
        var latest = due;
        var missed = 0;

        // Bounded: one step per occurrence, and there is at most one a day.
        for (var i = 0; i < CadenceSchedule.HorizonDays * 2; i++)
        {
            var next = CadenceSchedule.NextAfter(Cadence, zone, StartDate, EndDate, latest);
            if (next is not { } n || n > nowUtc) break;
            missed++;
            latest = n;
        }

        var fresh = nowUtc - latest <= LateLimit;
        if (!fresh) missed++;

        // From now, not from the occurrence: everything up to now has been
        // decided (posted or skipped), and the next is the first one after it.
        return new OccurrencePlan(
            fresh ? latest : null,
            missed,
            CadenceSchedule.NextAfter(Cadence, zone, StartDate, EndDate, nowUtc));
    }

    /// <summary>Records what the scheduler did with a due occurrence and moves on.</summary>
    /// <remarks>Ends the series itself when there is nothing left (past the end date, out of dates).</remarks>
    public void Advance(OccurrencePlan plan, DateTime nowUtc)
    {
        if (plan.PostAtUtc is { } posted) LastOccurrenceUtc = posted;
        if (plan.Missed > 0)
        {
            MissedCount += plan.Missed;
            LastMissedAtUtc = nowUtc;
        }

        NextOccurrenceUtc = plan.NextUtc;
        if (plan.NextUtc is null)
        {
            Status = SeriesStatus.Ended;
            StatusReason = "Every scheduled date has been posted.";
        }
        Changed();
    }

    /// <summary>The next occurrence after an instant, or null.</summary>
    public DateTime? NextFrom(DateTime afterUtc) =>
        CadenceSchedule.NextAfter(Cadence, Zone(), StartDate, EndDate, afterUtc);

    /// <summary>The next few occurrences after an instant (preview, calendar).</summary>
    public IReadOnlyList<DateTime> Upcoming(DateTime afterUtc, int count) =>
        CadenceSchedule.Upcoming(Cadence, Zone(), StartDate, EndDate, afterUtc, count);

    public TimeZoneInfo Zone() => ResolveZone(TimeZoneId) ?? TimeZoneInfo.Utc;

    /// <summary>
    /// The stable id of the post for one occurrence: the same series and the
    /// same instant always give the same id, which is what stops two schedulers
    /// creating it twice and makes a retry reuse it.
    /// </summary>
    public string OccurrenceId(DateTime occurrenceUtc) =>
        $"ser_{Id}_{DateTime.SpecifyKind(occurrenceUtc, DateTimeKind.Utc):yyyyMMdd'T'HHmm'Z'}";

    /// <summary>An IANA (or Windows) zone id, or null if this machine does not know it.</summary>
    public static TimeZoneInfo? ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out var zone) ? zone : null;
    }

    private void Changed()
    {
        Revision++;
        Touch();
    }
}

/// <summary>One account a series posts to.</summary>
public sealed record SeriesDestination(string ConnectionId, SocialPlatform Platform);
