using Acme.Application.BrandKit;
using MongoDB.Driver;
using DomainBrandKit = Acme.Domain.BrandKit.BrandKit;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoBrandKitRepository : IBrandKitRepository
{
    private const string CollectionName = "brand_kits";
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
