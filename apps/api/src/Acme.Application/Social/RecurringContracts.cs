using Acme.Domain.Social;

namespace Acme.Application.Social;

/// <summary>Recurring series storage.</summary>
public interface IRecurringSeriesRepository
{
    Task<RecurringSeries?> FindByIdAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<RecurringSeries>> ListByOwnerAsync(string ownerId, CancellationToken ct);
    Task AddAsync(RecurringSeries series, CancellationToken ct);

    /// <summary>
    /// Saves only if nobody else has saved since this copy was read.
    /// </summary>
    /// <param name="expectedRevision">The revision the caller read, before its change.</param>
    /// <returns>False when someone got there first; reload and decide again.</returns>
    Task<bool> TryUpdateAsync(RecurringSeries series, int expectedRevision, CancellationToken ct);

    /// <summary>Active series for an owner, oldest first. The plan's allowance goes to the oldest.</summary>
    Task<IReadOnlyList<RecurringSeries>> ListActiveByOwnerAsync(string ownerId, CancellationToken ct);

    /// <summary>Active series whose next occurrence is at or before <paramref name="nowUtc"/>, earliest first.</summary>
    Task<IReadOnlyList<RecurringSeries>> ListDueAsync(DateTime nowUtc, int limit, CancellationToken ct);
}

/// <summary>
/// The series' view of <c>scheduled_posts</c>: occurrences are ordinary
/// scheduled posts with a series id. Kept apart from
/// <see cref="IScheduledPostRepository"/> so the one-off publish path is not
/// touched.
/// </summary>
public interface ISeriesOccurrenceStore
{
    /// <summary>
    /// Inserts an occurrence. False if one with the same id already exists —
    /// another scheduler created it first, which is the outcome, not an error.
    /// </summary>
    Task<bool> TryAddAsync(ScheduledPost occurrence, CancellationToken ct);

    /// <summary>
    /// Cancels this series' occurrences that the worker has not started:
    /// still Scheduled, not leased, no target attempted. Used on pause, end
    /// and edit, so the change applies to everything not yet sent.
    /// </summary>
    /// <returns>How many were cancelled.</returns>
    Task<int> CancelUnstartedAsync(string seriesId, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Cancels unstarted occurrences, of any series, due before
    /// <paramref name="cutoffUtc"/> (their own scheduled time, so one the
    /// pharmacy rescheduled is judged by its new time) — created but never picked up, typically
    /// because the server stopped between the two. Past the late limit they
    /// must not go out.
    /// </summary>
    Task<int> SkipStaleAsync(DateTime cutoffUtc, DateTime nowUtc, CancellationToken ct);

    /// <summary>Recent occurrences of one series, newest first.</summary>
    Task<IReadOnlyList<ScheduledPost>> ListForSeriesAsync(string seriesId, int take, CancellationToken ct);

    /// <summary>
    /// Claims one finished occurrence whose outcome has not been reported,
    /// marking it reported in the same operation. Null when there is none.
    /// </summary>
    /// <param name="sinceUtc">Ignore anything finished before this, so a new deployment does not email history.</param>
    Task<ScheduledPost?> ClaimNextUnreportedAsync(DateTime sinceUtc, DateTime nowUtc, CancellationToken ct);
}

/// <summary>Sends the pharmacy the emails about its series. Never throws.</summary>
public interface ISeriesNotifier
{
    Task OccurrenceFinishedAsync(RecurringSeries series, ScheduledPost occurrence, CancellationToken ct);
    Task SeriesPausedAsync(RecurringSeries series, string reason, CancellationToken ct);
}
