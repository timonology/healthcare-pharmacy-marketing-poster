using Acme.Application.Common;
using Acme.Application.Posters;
using Acme.Domain.Posters;
using MongoDB.Driver;
using Acme.Domain.Subscriptions;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoPosterRepository : IPosterRepository
{
    private const string CollectionName = "posters";
    private readonly IMongoCollection<PosterDocument> _collection;

    public MongoPosterRepository(MongoContext ctx)
    {
        // Indexes are declared centrally in MongoIndexInitializer.
        _collection = ctx.Collection<PosterDocument>(CollectionName);
    }

    public async Task<(long Total, long ThisMonth)> CountAllAsync(CancellationToken ct)
    {
        var monthStart = BillingMonth.StartUtc();

        var total = _collection.CountDocumentsAsync(
            Builders<PosterDocument>.Filter.Empty, cancellationToken: ct);
        var thisMonth = _collection.CountDocumentsAsync(
            Builders<PosterDocument>.Filter.Gte(x => x.CreatedAtUtc, monthStart),
            cancellationToken: ct);

        await Task.WhenAll(total, thisMonth);
        return (total.Result, thisMonth.Result);
    }

    public async Task<IReadOnlyList<Poster>> ListAsync(PosterQuery query, CancellationToken ct)
    {
        var docs = await _collection
            .Find(BuildFilter(query))
            .Sort(ListWindowMongo.Sort<PosterDocument>(x => x.UpdatedAtUtc, x => x.Id, query.Window ?? ListWindow.Default))
            .Skip(query.Skip)
            .Limit(query.Take)
            .ToListAsync(ct);
        return docs.Select(PosterMapper.Hydrate).ToList();
    }

    public async Task<int> CountAsync(PosterQuery query, CancellationToken ct) =>
        (int)await _collection.CountDocumentsAsync(BuildFilter(query), cancellationToken: ct);

    public async Task<Poster?> FindByIdAsync(string id, CancellationToken ct)
    {
        var doc = await _collection.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc is null ? null : PosterMapper.Hydrate(doc);
    }

    public async Task<IReadOnlyList<Poster>> FindManyByIdsAsync(
        string ownerId, IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return Array.Empty<Poster>();

        var fb = Builders<PosterDocument>.Filter;
        var filter = fb.Eq(x => x.OwnerId, ownerId) & fb.In(x => x.Id, ids);

        var docs = await _collection.Find(filter).ToListAsync(ct);
        return docs.Select(PosterMapper.Hydrate).ToList();
    }

    public Task AddAsync(Poster poster, CancellationToken ct) =>
        _collection.InsertOneAsync(PosterMapper.ToDocument(poster), cancellationToken: ct);

    public Task UpdateAsync(Poster poster, CancellationToken ct) =>
        _collection.ReplaceOneAsync(
            x => x.Id == poster.Id,
            PosterMapper.ToDocument(poster),
            cancellationToken: ct);

    public Task DeleteAsync(string id, CancellationToken ct) =>
        _collection.DeleteOneAsync(x => x.Id == id, ct);

    private static FilterDefinition<PosterDocument> BuildFilter(PosterQuery q)
    {
        var b = Builders<PosterDocument>.Filter;
        var filter = b.Eq(x => x.OwnerId, q.OwnerId);

        if (q.Status is { } status)
            filter &= b.Eq(x => x.Status, (int)status);

        if (!string.IsNullOrWhiteSpace(q.Search))
            filter &= b.Text(q.Search);

        if (q.Window is { HasRange: true } w)
            filter &= ListWindowMongo.Range<PosterDocument>(x => x.UpdatedAtUtc, w);

        return filter;
    }
}
