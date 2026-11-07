using Acme.Application.Social;
using Acme.Domain.Social;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoRecurringSeriesRepository(MongoContext ctx) : IRecurringSeriesRepository
{
    public const string CollectionName = "recurring_series";

    private IMongoCollection<RecurringSeriesDocument> Col =>
        ctx.Collection<RecurringSeriesDocument>(CollectionName);

    public async Task<RecurringSeries?> FindByIdAsync(string id, CancellationToken ct)
    {
        var doc = await Col.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc is null ? null : RecurringSeriesMapper.Hydrate(doc);
    }

    public async Task<IReadOnlyList<RecurringSeries>> ListByOwnerAsync(string ownerId, CancellationToken ct)
    {
        var docs = await Col.Find(x => x.OwnerId == ownerId)
            // Live ones first (Active 0, Paused 1, Ended 2), newest within each.
            .SortBy(x => x.Status).ThenByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Limit(200)
            .ToListAsync(ct);
        return docs.Select(RecurringSeriesMapper.Hydrate).ToList();
    }

    public Task AddAsync(RecurringSeries series, CancellationToken ct) =>
        Col.InsertOneAsync(RecurringSeriesMapper.ToDocument(series), cancellationToken: ct);

    public async Task<bool> TryUpdateAsync(RecurringSeries series, int expectedRevision, CancellationToken ct)
    {
        var fb = Builders<RecurringSeriesDocument>.Filter;
        var filter = fb.Eq(x => x.Id, series.Id) & fb.Eq(x => x.Revision, expectedRevision);
        var result = await Col.ReplaceOneAsync(filter, RecurringSeriesMapper.ToDocument(series), new ReplaceOptions(), ct);
        return result.MatchedCount == 1;
    }

    public async Task<IReadOnlyList<RecurringSeries>> ListActiveByOwnerAsync(string ownerId, CancellationToken ct)
    {
        var docs = await Col
            .Find(x => x.OwnerId == ownerId && x.Status == (int)SeriesStatus.Active)
            .SortBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .ToListAsync(ct);
        return docs.Select(RecurringSeriesMapper.Hydrate).ToList();
    }

    public async Task<IReadOnlyList<RecurringSeries>> ListDueAsync(DateTime nowUtc, int limit, CancellationToken ct)
    {
        var fb = Builders<RecurringSeriesDocument>.Filter;
        var filter = fb.Eq(x => x.Status, (int)SeriesStatus.Active)
                     & fb.Ne(x => x.NextOccurrenceUtc, null)
                     & fb.Lte(x => x.NextOccurrenceUtc, nowUtc);
        var docs = await Col.Find(filter)
            .SortBy(x => x.NextOccurrenceUtc)
            .Limit(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);
        return docs.Select(RecurringSeriesMapper.Hydrate).ToList();
    }
}

public sealed class MongoSeriesOccurrenceStore(MongoContext ctx) : ISeriesOccurrenceStore
{
    private IMongoCollection<ScheduledPostDocument> Col =>
        ctx.Collection<ScheduledPostDocument>("scheduled_posts");

    public async Task<bool> TryAddAsync(ScheduledPost occurrence, CancellationToken ct)
    {
        try
        {
            await Col.InsertOneAsync(SocialMapper.ToDocument(occurrence), cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another scheduler made this exact occurrence first. The id is
            // the series and the instant, so "exists" means "already queued".
            return false;
        }
    }

    /// <summary>Scheduled, unleased, and no target ever attempted: the worker has never touched it.</summary>
    /// <remarks>
    /// "Ever" is the last-attempt time, not the attempt count. A pharmacy's
    /// manual Retry resets the count to zero but keeps the time, and a retry
    /// they asked for must not be swept up as a stale or paused occurrence.
    /// </remarks>
    private static FilterDefinition<ScheduledPostDocument> Unstarted()
    {
        var fb = Builders<ScheduledPostDocument>.Filter;
        return fb.Eq(x => x.Status, (int)PublishStatus.Scheduled)
               & fb.Eq(x => x.LeaseHolder, null)
               & fb.Not(fb.ElemMatch(x => x.Targets, t => t.AttemptCount > 0 || t.LastAttemptAtUtc != null));
    }

    private static UpdateDefinition<ScheduledPostDocument> CancelUpdate(DateTime nowUtc) =>
        Builders<ScheduledPostDocument>.Update
            .Set(x => x.Status, (int)PublishStatus.Cancelled)
            .Set(x => x.NextAttemptAtUtc, null)
            .Set(x => x.UpdatedAtUtc, nowUtc)
            // Nothing to tell anyone about: it was never sent.
            .Set(x => x.SeriesNotifiedAtUtc, nowUtc);

    public async Task<int> CancelUnstartedAsync(string seriesId, DateTime nowUtc, CancellationToken ct)
    {
        var filter = Builders<ScheduledPostDocument>.Filter.Eq(x => x.SeriesId, seriesId) & Unstarted();
        var result = await Col.UpdateManyAsync(filter, CancelUpdate(nowUtc), cancellationToken: ct);
        return (int)result.ModifiedCount;
    }

    public async Task<int> SkipStaleAsync(DateTime cutoffUtc, DateTime nowUtc, CancellationToken ct)
    {
        var fb = Builders<ScheduledPostDocument>.Filter;
        // The post's own due time, not the series instant: a pharmacy who
        // rescheduled one occurrence to later in the day moved its deadline
        // with it. A manual retry has no due time (null), so never matches.
        var filter = fb.Ne(x => x.SeriesId, null)
                     & fb.Ne(x => x.ScheduledForUtc, null)
                     & fb.Lt(x => x.ScheduledForUtc, cutoffUtc)
                     & Unstarted();
        var result = await Col.UpdateManyAsync(filter, CancelUpdate(nowUtc), cancellationToken: ct);
        return (int)result.ModifiedCount;
    }

    public async Task<IReadOnlyList<ScheduledPost>> ListForSeriesAsync(string seriesId, int take, CancellationToken ct)
    {
        var docs = await Col.Find(x => x.SeriesId == seriesId)
            .SortByDescending(x => x.OccurrenceAtUtc)
            .Limit(Math.Clamp(take, 1, 100))
            .ToListAsync(ct);
        return docs.Select(SocialMapper.Hydrate).ToList();
    }

    public async Task<ScheduledPost?> ClaimNextUnreportedAsync(DateTime sinceUtc, DateTime nowUtc, CancellationToken ct)
    {
        var fb = Builders<ScheduledPostDocument>.Filter;
        var filter = fb.Ne(x => x.SeriesId, null)
                     & fb.In(x => x.Status, new[]
                     {
                         (int)PublishStatus.Published,
                         (int)PublishStatus.PartiallyPublished,
                         (int)PublishStatus.Failed,
                     })
                     & fb.Eq(x => x.SeriesNotifiedAtUtc, null)
                     & fb.Gte(x => x.CompletedAtUtc, sinceUtc);

        var doc = await Col.FindOneAndUpdateAsync(
            filter,
            Builders<ScheduledPostDocument>.Update.Set(x => x.SeriesNotifiedAtUtc, nowUtc),
            new FindOneAndUpdateOptions<ScheduledPostDocument>
            {
                ReturnDocument = ReturnDocument.After,
                Sort = Builders<ScheduledPostDocument>.Sort.Ascending(x => x.CompletedAtUtc),
            },
            ct);

        return doc is null ? null : SocialMapper.Hydrate(doc);
    }
}
