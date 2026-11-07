using Acme.Application.Common;
using Acme.Application.Videos;
using Acme.Domain.Videos;
using MongoDB.Driver;
using Acme.Domain.Subscriptions;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoVideoRepository(MongoContext ctx) : IVideoRepository
{
    private IMongoCollection<VideoDocument> Col => ctx.Collection<VideoDocument>("videos");

    public async Task<IReadOnlyList<Video>> ListByOwnerAsync(string ownerId, CancellationToken ct)
    {
        var docs = await Col.Find(x => x.OwnerId == ownerId)
            .SortByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        return docs.Select(VideoMapper.Hydrate).ToList();
    }

    public async Task<IReadOnlyList<Video>> ListAsync(string ownerId, ListWindow window, CancellationToken ct)
    {
        var filter = Builders<VideoDocument>.Filter.Eq(x => x.OwnerId, ownerId)
            & ListWindowMongo.Range<VideoDocument>(x => x.CreatedAtUtc, window);
        var docs = await Col.Find(filter)
            .Sort(ListWindowMongo.Sort<VideoDocument>(x => x.CreatedAtUtc, x => x.Id, window))
            .ToListAsync(ct);
        return docs.Select(VideoMapper.Hydrate).ToList();
    }

    public async Task<Video?> FindByIdAsync(string id, CancellationToken ct)
    {
        var doc = await Col.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc is null ? null : VideoMapper.Hydrate(doc);
    }

    public async Task<IReadOnlyList<Video>> FindManyByIdsAsync(
        string ownerId, IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return Array.Empty<Video>();

        var fb = Builders<VideoDocument>.Filter;
        var filter = fb.Eq(x => x.OwnerId, ownerId) & fb.In(x => x.Id, ids);

        var docs = await Col.Find(filter).ToListAsync(ct);
        return docs.Select(VideoMapper.Hydrate).ToList();
    }

    public async Task<VideoShotSource?> FindShotSourceAsync(string id, CancellationToken ct)
    {
        var doc = await Col.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        if (doc is null) return null;

        return new VideoShotSource(
            doc.OwnerId,
            doc.MetadataJson,
            (doc.ShotTimeline ?? new List<ShotTimelineDocument>())
                .Select(t => new ShotTiming(t.Segment, t.Start, t.End, t.ThumbKey))
                .ToList(),
            (doc.ClipSources ?? new List<ClipSourceDocument>())
                .Select(c => new ShotClip(c.Segment, c.Role, c.Shows))
                .ToList());
    }

    public async Task<int> CountByOwnerThisMonthAsync(string ownerId, CancellationToken ct)
    {
        var monthStart = BillingMonth.StartUtc();
        var fb = Builders<VideoDocument>.Filter;
        var filter = fb.Eq(x => x.OwnerId, ownerId) & fb.Gte(x => x.CreatedAtUtc, monthStart);
        return (int)await Col.CountDocumentsAsync(filter, cancellationToken: ct);
    }

    public Task<long> CountAllAsync(CancellationToken ct) =>
        Col.CountDocumentsAsync(Builders<VideoDocument>.Filter.Empty, cancellationToken: ct);

    public Task<long> CountAllThisMonthAsync(CancellationToken ct)
    {
        var monthStart = BillingMonth.StartUtc();
        return Col.CountDocumentsAsync(
            Builders<VideoDocument>.Filter.Gte(x => x.CreatedAtUtc, monthStart),
            cancellationToken: ct);
    }

    // Pending-queue scanning moved to the Python ai-service worker
    // (apps/ai-service/app/video_agent/worker.py) — it uses Motor's
    // find_one_and_update to atomically claim a row Pending → Rendering.

    public Task AddAsync(Video video, CancellationToken ct) =>
        Col.InsertOneAsync(VideoMapper.ToDocument(video), cancellationToken: ct);

    /// <summary>
    /// Writes the fields the .NET side owns, and only those.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a document replace.</b> It was, and a replace is built from
    /// <c>VideoMapper.ToDocument</c>, which maps the twenty-one fields the
    /// domain knows about and none of the seven the Python render worker writes
    /// — the script, the generated title and description, the tags, the
    /// metadata with its segment timings, and the two AI-imagery cost counters.
    /// </para>
    /// <para>
    /// So every write from this side erased them. Renaming a video destroyed
    /// its script and the record of what it cost to make, silently, with the
    /// rename appearing to work perfectly. Retry did the same, which mattered
    /// less only because a re-render writes them back.
    /// </para>
    /// <para>
    /// Two processes write this document and each owns part of it. The worker
    /// already updates its half by named field; this is the same discipline
    /// applied to ours, and the same fix the campaign queue needed when its
    /// progress writes were trampling a pharmacist's Stop.
    /// </para>
    /// <para>
    /// <c>_id</c>, <c>ownerId</c> and <c>createdAtUtc</c> are absent
    /// deliberately. They are facts fixed at insert, and an update that could
    /// write <c>ownerId</c> is an update that could move a video from one
    /// pharmacy to another.
    /// </para>
    /// </remarks>
    public Task UpdateAsync(Video video, CancellationToken ct)
    {
        var d = VideoMapper.ToDocument(video);
        var u = Builders<VideoDocument>.Update
            .Set(x => x.Name, d.Name)
            .Set(x => x.Topic, d.Topic)
            .Set(x => x.StyleTone, d.StyleTone)
            .Set(x => x.Source, d.Source)
            .Set(x => x.Status, d.Status)
            .Set(x => x.ScriptSource, d.ScriptSource)
            .Set(x => x.VisualStyle, d.VisualStyle)
            .Set(x => x.Description, d.Description)
            .Set(x => x.CustomScript, d.CustomScript)
            .Set(x => x.VoiceId, d.VoiceId)
            // .NET-owned, like Brand and Refine below: written at create and on
            // retry, only ever read by the worker. Leaving it out would have a
            // rename drop the pharmacist's own photographs from the next attempt.
            .Set(x => x.Images, d.Images)
            .Set(x => x.Aspect, d.Aspect)
            .Set(x => x.DurationSeconds, d.DurationSeconds)
            .Set(x => x.BlobKey, d.BlobKey)
            .Set(x => x.ThumbnailBlobKey, d.ThumbnailBlobKey)
            .Set(x => x.Note, d.Note)
            .Set(x => x.LeaseOwner, d.LeaseOwner)
            .Set(x => x.LeaseExpiresAtUtc, d.LeaseExpiresAtUtc)
            // The .NET side owns this: stamped at create, re-resolved on Retry.
            // Leaving it out would mean a rename silently un-brands the next
            // attempt — the same fault this method was rewritten to stop.
            .Set(x => x.Brand, d.Brand)
            // Also .NET-owned. The worker reads the stage and the instruction
            // and writes neither, so this side is free to overwrite it.
            .Set(x => x.Refine, d.Refine)
            .Set(x => x.UpdatedAtUtc, d.UpdatedAtUtc);

        return Col.UpdateOneAsync(x => x.Id == video.Id, u, cancellationToken: ct);
    }

    public Task DeleteAsync(string id, CancellationToken ct) =>
        Col.DeleteOneAsync(x => x.Id == id, ct);
}
