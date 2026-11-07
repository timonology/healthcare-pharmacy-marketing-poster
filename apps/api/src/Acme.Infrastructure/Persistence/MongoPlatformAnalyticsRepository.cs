using Acme.Application.Admin;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

/// <inheritdoc />
public sealed class MongoPlatformAnalyticsRepository(MongoContext ctx)
    : IPlatformAnalyticsRepository
{
    private IMongoCollection<BsonDocument> Posters => ctx.Collection<BsonDocument>("posters");
    private IMongoCollection<BsonDocument> Videos => ctx.Collection<BsonDocument>("videos");
    private IMongoCollection<BsonDocument> Campaigns => ctx.Collection<BsonDocument>("campaigns");

    public Task<IReadOnlyList<DailyCount>> PostersPerDayAsync(DateTime since, CancellationToken ct) =>
        PerDayAsync(Posters, "createdAtUtc", since, ct);

    public Task<IReadOnlyList<DailyCount>> VideosPerDayAsync(DateTime since, CancellationToken ct) =>
        PerDayAsync(Videos, "createdAtUtc", since, ct);

    /// <summary>
    /// Campaigns bucketed by when they were SENT, not created.
    /// </summary>
    /// <remarks>
    /// A campaign drafted in January and sent in March is March's activity. The
    /// $match on the same field also excludes drafts for free, since an unsent
    /// campaign has no sentAtUtc.
    /// </remarks>
    public Task<IReadOnlyList<DailyCount>> CampaignsPerDayAsync(DateTime since, CancellationToken ct) =>
        PerDayAsync(Campaigns, "sentAtUtc", since, ct);

    /// <summary>
    /// Counts documents per UTC day.
    /// </summary>
    /// <remarks>
    /// Written as an untyped pipeline rather than the LINQ provider. $dateToString
    /// has no clean typed equivalent, and expressing it through the driver's
    /// expression translator is exactly the kind of code that compiles fine and
    /// then throws at runtime on a version bump.
    ///
    /// Grouping on a formatted string rather than $dayOfYear keeps the bucket
    /// unambiguous across a year boundary — day 5 of 2026 and day 5 of 2027 must
    /// not collapse into one point.
    /// </remarks>
    private static async Task<IReadOnlyList<DailyCount>> PerDayAsync(
        IMongoCollection<BsonDocument> collection,
        string dateField,
        DateTime since,
        CancellationToken ct)
    {
        // PipelineDefinition.Create rather than relying on the implicit
        // conversion from BsonDocument[] — explicit is unambiguous, and this is
        // the first untyped pipeline in the codebase so there is no local
        // precedent to lean on.
        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(
            new BsonDocument("$match", new BsonDocument
            {
                { dateField, new BsonDocument("$gte", since) },
            }),
            new BsonDocument("$group", new BsonDocument
            {
                {
                    "_id",
                    new BsonDocument("$dateToString", new BsonDocument
                    {
                        { "format", "%Y-%m-%d" },
                        { "date", "$" + dateField },
                        { "timezone", "UTC" },
                    })
                },
                { "count", new BsonDocument("$sum", 1) },
            }),
            new BsonDocument("$sort", new BsonDocument("_id", 1)));

        var rows = await collection
            .Aggregate<BsonDocument>(pipeline, cancellationToken: ct)
            .ToListAsync(ct);

        return rows
            .Select(r => new DailyCount(
                r["_id"].AsString,
                r["count"].ToInt64()))
            .ToList();
    }

    /// <summary>
    /// Combined output per account.
    /// </summary>
    /// <remarks>
    /// All three collections are grouped in full rather than taking a top-N from
    /// each and merging. Merging pre-truncated lists gives the wrong answer: an
    /// account ranked just outside the cut in every collection can still have
    /// the highest combined total, and would be invisible. Grouping by owner
    /// yields one row per account, which is bounded by the number of pharmacies
    /// — small enough that correctness is the cheaper option.
    /// </remarks>
    public async Task<IReadOnlyList<OwnerActivity>> TopOwnersAsync(
        DateTime since, int limit, CancellationToken ct)
    {
        var posters = CountByOwnerAsync(Posters, "createdAtUtc", since, ct);
        var videos = CountByOwnerAsync(Videos, "createdAtUtc", since, ct);
        var campaigns = CountByOwnerAsync(Campaigns, "sentAtUtc", since, ct);

        await Task.WhenAll(posters, videos, campaigns);

        var owners = new HashSet<string>(posters.Result.Keys);
        owners.UnionWith(videos.Result.Keys);
        owners.UnionWith(campaigns.Result.Keys);

        return owners
            .Select(id => new OwnerActivity(
                id,
                posters.Result.GetValueOrDefault(id),
                videos.Result.GetValueOrDefault(id),
                campaigns.Result.GetValueOrDefault(id)))
            .OrderByDescending(o => o.Total)
            .ThenBy(o => o.OwnerId, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 50))
            .ToList();
    }

    private static async Task<Dictionary<string, long>> CountByOwnerAsync(
        IMongoCollection<BsonDocument> collection,
        string dateField,
        DateTime since,
        CancellationToken ct)
    {
        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(
            new BsonDocument("$match", new BsonDocument
            {
                { dateField, new BsonDocument("$gte", since) },
            }),
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", "$ownerId" },
                { "count", new BsonDocument("$sum", 1) },
            }));

        var rows = await collection
            .Aggregate<BsonDocument>(pipeline, cancellationToken: ct)
            .ToListAsync(ct);

        var result = new Dictionary<string, long>();
        foreach (var row in rows)
        {
            // Documents predating the ownerId field, or written without one,
            // group under null. They are real activity but belong to nobody we
            // can name, so they are dropped rather than shown as a blank row.
            var id = row["_id"];
            if (id.IsBsonNull) continue;
            result[id.AsString] = row["count"].ToInt64();
        }
        return result;
    }
}
