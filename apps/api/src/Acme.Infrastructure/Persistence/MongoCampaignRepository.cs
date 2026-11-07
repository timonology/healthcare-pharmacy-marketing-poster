using Acme.Application.Common;
using Acme.Application.Campaigns;
using Acme.Domain.Campaigns;
using MongoDB.Bson;
using MongoDB.Driver;
using Acme.Domain.Subscriptions;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoCampaignRepository : ICampaignRepository
{
    private const string CollectionName = "campaigns";
    private readonly IMongoCollection<CampaignDocument> _collection;

    public MongoCampaignRepository(MongoContext ctx)
    {
        _collection = ctx.Collection<CampaignDocument>(CollectionName);
    }

    public async Task<IReadOnlyList<Campaign>> ListByOwnerAsync(string ownerId, CancellationToken ct)
    {
        var docs = await _collection
            .Find(x => x.OwnerId == ownerId)
            .SortByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        return docs.Select(CampaignMapper.Hydrate).ToList();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One aggregation: the listing date is computed in the database
    /// (<c>sentAtUtc</c>, else <c>createdAtUtc</c>), then matched and sorted on.
    /// Only <c>$addFields</c>, <c>$ifNull</c>, <c>$match</c> and <c>$sort</c> —
    /// all available on the hosted Mongo API — and the owner match runs first,
    /// so it uses the owner index before anything is computed.
    /// </remarks>
    public async Task<IReadOnlyList<Campaign>> ListAsync(string ownerId, ListWindow window, CancellationToken ct)
    {
        const string listedAt = "_listedAtUtc";

        var range = new BsonDocument();
        if (window.FromUtc is { } from) range["$gte"] = from;
        if (window.ToUtc is { } to) range["$lt"] = to;

        var dir = window.Ascending ? 1 : -1;

        var pipeline = _collection.Aggregate()
            .Match(Builders<CampaignDocument>.Filter.Eq(x => x.OwnerId, ownerId))
            .AppendStage<CampaignDocument>(new BsonDocument("$addFields", new BsonDocument(
                listedAt, new BsonDocument("$ifNull", new BsonArray { "$sentAtUtc", "$createdAtUtc" }))));

        if (range.ElementCount > 0)
            pipeline = pipeline.AppendStage<CampaignDocument>(
                new BsonDocument("$match", new BsonDocument(listedAt, range)));

        var docs = await pipeline
            .AppendStage<CampaignDocument>(new BsonDocument("$sort", new BsonDocument
            {
                { listedAt, dir },
                { "_id", dir },
            }))
            // Dropped so the extra field never reaches the mapper, whatever the
            // document conventions say about unknown elements.
            .AppendStage<CampaignDocument>(new BsonDocument("$project", new BsonDocument(listedAt, 0)))
            .ToListAsync(ct);

        return docs.Select(CampaignMapper.Hydrate).ToList();
    }

    public async Task<CampaignTotals> GetPlatformTotalsAsync(CancellationToken ct)
    {
        var monthStart = BillingMonth.StartUtc();

        var f = Builders<CampaignDocument>.Filter;
        var sent = f.Ne(x => x.SentAtUtc, null);

        var sentTotal = _collection.CountDocumentsAsync(sent, cancellationToken: ct);
        var sentThisMonth = _collection.CountDocumentsAsync(
            f.And(sent, f.Gte(x => x.SentAtUtc, monthStart)), cancellationToken: ct);

        // Summed server-side. Pulling every campaign back to add up SentCount in
        // memory would grow linearly with the platform, on a dashboard that
        // loads on every admin page view.
        var recipients = _collection.Aggregate()
            .Match(sent)
            .Group(_ => 1, g => new { Total = g.Sum(x => x.SentCount) })
            .FirstOrDefaultAsync(ct);

        await Task.WhenAll(sentTotal, sentThisMonth, recipients);

        return new CampaignTotals(
            sentTotal.Result,
            sentThisMonth.Result,
            recipients.Result?.Total ?? 0);
    }

    public async Task<Campaign?> FindByIdAsync(string id, CancellationToken ct)
    {
        var doc = await _collection.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc is null ? null : CampaignMapper.Hydrate(doc);
    }

    public Task AddAsync(Campaign campaign, CancellationToken ct) =>
        _collection.InsertOneAsync(CampaignMapper.ToDocument(campaign), cancellationToken: ct);

    public Task UpdateAsync(Campaign campaign, CancellationToken ct) =>
        _collection.ReplaceOneAsync(
            x => x.Id == campaign.Id,
            CampaignMapper.ToDocument(campaign),
            cancellationToken: ct);

    /// <inheritdoc/>
    public async Task<CampaignStatus?> SaveProgressAsync(Campaign campaign, CancellationToken ct)
    {
        var report = campaign.DeliveryReport
            .Select(r => new RecipientResultDocument
            {
                Recipient = r.Recipient,
                Status = (int)r.Status,
                Reason = r.Reason,
                AtUtc = r.AtUtc,
            })
            .ToList();

        // Named fields, not a replacement. `status` is absent from this list on
        // purpose and must stay absent: it is the field somebody else may have
        // just changed, and the whole point of this method is not to trample it.
        var progress = Builders<CampaignDocument>.Update
            .Set(x => x.DeliveryReport, report)
            .Set(x => x.SentCount, campaign.SentCount)
            .Set(x => x.LeaseExpiresAtUtc, campaign.LeaseExpiresAtUtc)
            .Set(x => x.UpdatedAtUtc, DateTime.UtcNow);

        var doc = await _collection.FindOneAndUpdateAsync(
            Builders<CampaignDocument>.Filter.Eq(x => x.Id, campaign.Id),
            progress,
            new FindOneAndUpdateOptions<CampaignDocument>
            {
                // After, so the status returned is the one that is true now
                // rather than the one from before this write.
                ReturnDocument = ReturnDocument.After,
            },
            ct);

        // A stopped campaign keeps the lease expiry this write renewed. Nothing
        // reads a lease on a stopped campaign — the claim filter only looks at
        // Queued and Sending, and Queue() clears it — so it is untidy rather
        // than wrong, and worth less than another round trip to remove.
        return doc is null ? null : (CampaignStatus?)doc.Status;
    }

    /// <inheritdoc/>
    public async Task<Campaign?> ClaimNextForDispatchAsync(
        string worker, DateTime leaseUntilUtc, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var b = Builders<CampaignDocument>.Filter;

        // Queued, or Sending with a lapsed lease. The second is the recovery
        // case: a worker that was killed left the campaign claimed, and nothing
        // else can release it.
        //
        // `LeaseExpiresAtUtc == null` is included deliberately. Campaigns
        // written before dispatch moved off the request thread are `Sending`
        // with no lease at all, and they are exactly the ones that have been
        // stuck since — permanently unfinishable, and refused by Retry because
        // it only accepts `Failed`. This is what unsticks them.
        var claimable = b.Or(
            b.Eq(x => x.Status, (int)CampaignStatus.Queued),
            b.And(
                b.Eq(x => x.Status, (int)CampaignStatus.Sending),
                b.Or(
                    b.Eq(x => x.LeaseExpiresAtUtc, null),
                    b.Lt(x => x.LeaseExpiresAtUtc, now))));

        var take = Builders<CampaignDocument>.Update
            .Set(x => x.Status, (int)CampaignStatus.Sending)
            .Set(x => x.LeaseOwner, worker)
            .Set(x => x.LeaseExpiresAtUtc, leaseUntilUtc)
            .Set(x => x.UpdatedAtUtc, now);

        // FindOneAndUpdate is one round trip and one document-level atomic
        // operation, so two workers racing cannot both win.
        var doc = await _collection.FindOneAndUpdateAsync(
            claimable,
            take,
            new FindOneAndUpdateOptions<CampaignDocument>
            {
                // The updated document, so the caller sees the lease it holds
                // rather than the state before the claim.
                ReturnDocument = ReturnDocument.After,
                // Oldest first: a campaign that has been waiting should not be
                // starved by a steady arrival of new ones.
                Sort = Builders<CampaignDocument>.Sort.Ascending(x => x.CreatedAtUtc),
            },
            ct);

        return doc is null ? null : CampaignMapper.Hydrate(doc);
    }

    /// <summary>
    /// Recipients this month counts against the plan: delivered, plus committed
    /// and waiting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The second half is new, and without it the monthly limit did not
    /// hold.</b> This used to sum <c>SentCount</c> over campaigns with a
    /// <c>SentAtUtc</c>, which was the whole truth while sending happened inside
    /// the request: by the time <c>POST /api/campaigns</c> returned, the
    /// campaign was finished and counted, so the next one saw it. Sending now
    /// queues, and neither field is written until a worker finishes — so a
    /// pharmacy on a 500/month plan could create five 500-recipient campaigns in
    /// as many seconds, each limit check reading 0, and send 2,500.
    /// </para>
    /// <para>
    /// Queued and Sending campaigns therefore contribute their outstanding
    /// recipients. A queue is a promise to send, and a quota that only counts
    /// what has already gone out cannot refuse one.
    /// </para>
    /// <para>
    /// Outstanding rather than total, for the retry case: a campaign that
    /// reached 700 of 1000 and is queued again contributes 300 here and its 700
    /// through <c>SentCount</c> — never both. The cost is a brief under-count
    /// mid-send, where recipients already delivered are no longer pending and
    /// <c>SentCount</c> is not yet written; it corrects itself when the campaign
    /// completes, and erring towards letting a pharmacy send is the right way
    /// round for a billing limit.
    /// </para>
    /// <para>
    /// Two concurrent requests can still both pass the check in the service.
    /// That race predates the queue and is unchanged by it; closing it properly
    /// means a per-owner counter incremented atomically, which is a different
    /// piece of work.
    /// </para>
    /// </remarks>
    public async Task<int> CountRecipientsThisMonthAsync(string ownerId, CancellationToken ct)
    {
        var monthStart = BillingMonth.StartUtc();
        var f = Builders<CampaignDocument>.Filter;

        // Finished campaigns, and stopped ones for what got out before the stop.
        // The two are disjoint: Stop() refuses a campaign that is already Sent,
        // so nothing carries a SentAtUtc and the Stopped status at once.
        var delivered = _collection
            .Find(f.And(
                f.Eq(x => x.OwnerId, ownerId),
                f.Or(
                    f.Gte(x => x.SentAtUtc, monthStart),
                    f.And(
                        f.Eq(x => x.Status, (int)CampaignStatus.Stopped),
                        f.Gte(x => x.UpdatedAtUtc, monthStart)))))
            .Project(x => x.SentCount)
            .ToListAsync(ct);

        // No date filter: a campaign that is queued or being sent is going out
        // now, whatever month it was first created in.
        var inFlight = _collection
            .Find(f.And(
                f.Eq(x => x.OwnerId, ownerId),
                f.In(x => x.Status, new[]
                {
                    (int)CampaignStatus.Queued,
                    (int)CampaignStatus.Sending,
                })))
            .ToListAsync(ct);

        await Task.WhenAll(delivered, inFlight);

        // Read in memory rather than aggregated server-side, unlike the platform
        // totals above. An owner has a handful of campaigns in flight at once —
        // usually none — where that query runs over every campaign on the
        // platform.
        var outstanding = inFlight.Result.Sum(doc =>
            doc.DeliveryReport.Count(r => r.Status == (int)RecipientStatus.Pending));

        return delivered.Result.Sum() + outstanding;
    }
}
