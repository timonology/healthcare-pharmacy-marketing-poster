using Acme.Application.BrandKit;
using MongoDB.Driver;
using DomainBrandKit = Acme.Domain.BrandKit.BrandKit;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoBrandKitRepository : IBrandKitRepository
{
    /// <summary>
    /// The one spelling of this collection's name.
    /// </summary>
    /// <remarks>
    /// Internal rather than private because it was already written out by hand
    /// in <see cref="MongoIndexInitializer"/> as well, and a third copy went
    /// into a migration as "brandKits" — camel-cased, plausible, and a
    /// different collection. Mongo creates a collection on first write, so that
    /// migration would have reported success, written every kit somewhere
    /// nothing reads, and cleared the source field it was moving from.
    /// </remarks>
    internal const string CollectionName = "brand_kits";
    private readonly IMongoCollection<BrandKitDocument> _collection;

    public MongoBrandKitRepository(MongoContext ctx)
    {
        // Indexes are declared centrally in MongoIndexInitializer.
        _collection = ctx.Collection<BrandKitDocument>(CollectionName);
    }

    public async Task<DomainBrandKit?> FindByOwnerAsync(string ownerId, CancellationToken ct)
    {
        var doc = await _collection.Find(x => x.OwnerId == ownerId).FirstOrDefaultAsync(ct);
        return doc is null ? null : BrandKitMapper.Hydrate(doc);
    }

    public async Task<DomainBrandKit?> FindByIdAsync(string id, CancellationToken ct)
    {
        var doc = await _collection.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc is null ? null : BrandKitMapper.Hydrate(doc);
    }

    public Task AddAsync(DomainBrandKit kit, CancellationToken ct) =>
        _collection.InsertOneAsync(BrandKitMapper.ToDocument(kit), cancellationToken: ct);

    public Task UpdateAsync(DomainBrandKit kit, CancellationToken ct) =>
        _collection.ReplaceOneAsync(
            x => x.Id == kit.Id,
            BrandKitMapper.ToDocument(kit),
            cancellationToken: ct);
}
