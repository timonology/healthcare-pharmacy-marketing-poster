namespace Acme.Application.Admin;

/// <param name="Date">UTC day, ISO-8601 (yyyy-MM-dd).</param>
public sealed record DailyCount(string Date, long Count);

/// <param name="OwnerId">
/// The pharmacy account. Names are resolved separately — the activity
/// collections store only the id, and joining across collections in Mongo for
/// a handful of rows costs more than a second targeted query.
/// </param>
public sealed record OwnerActivity(
    string OwnerId,
    long Posters,
    long Videos,
    long Campaigns)
{
    public long Total => Posters + Videos + Campaigns;
}

/// <summary>
/// Platform-wide activity aggregates for the admin analytics page.
/// </summary>
/// <remarks>
/// Separate from the per-user <c>AnalyticsService</c>, which answers "how is
/// this pharmacy doing" for the Pro-tier customer dashboard and is scoped to a
/// single owner. These queries deliberately span every account, so they are
/// only ever reachable from the admin surface.
///
/// Everything here is computed database-side. Loading documents to count them
/// in application code would scale with the platform on a page that is
/// refreshed casually.
/// </remarks>
public interface IPlatformAnalyticsRepository
{
    /// <summary>Documents created per UTC day, oldest first.</summary>
    /// <param name="since">Inclusive lower bound.</param>
    Task<IReadOnlyList<DailyCount>> PostersPerDayAsync(DateTime since, CancellationToken ct);

    Task<IReadOnlyList<DailyCount>> VideosPerDayAsync(DateTime since, CancellationToken ct);

    /// <summary>Campaigns actually sent per UTC day — drafts are excluded.</summary>
    Task<IReadOnlyList<DailyCount>> CampaignsPerDayAsync(DateTime since, CancellationToken ct);

    /// <summary>Busiest accounts by combined output since the given date.</summary>
    Task<IReadOnlyList<OwnerActivity>> TopOwnersAsync(
        DateTime since, int limit, CancellationToken ct);
}
