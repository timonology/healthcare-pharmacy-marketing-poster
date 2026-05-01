using Acme.Application.Posters;
using Acme.Domain.Posters;
using MongoDB.Driver;

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

    public async Task<IReadOnlyList<Poster>> ListAsync(PosterQuery query, CancellationToken ct)
    {
        var docs = await _collection
            .Find(BuildFilter(query))
            .SortByDescending(x => x.UpdatedAtUtc)
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

        return filter;
    }
}
