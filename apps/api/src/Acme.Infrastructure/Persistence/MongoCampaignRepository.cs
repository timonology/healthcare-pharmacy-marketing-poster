using Acme.Application.Campaigns;
using Acme.Domain.Campaigns;
using MongoDB.Driver;

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

    public async Task<int> CountRecipientsThisMonthAsync(string ownerId, CancellationToken ct)
    {
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var docs = await _collection
            .Find(x => x.OwnerId == ownerId && x.SentAtUtc >= monthStart)
            .Project(x => x.SentCount)
            .ToListAsync(ct);
        return docs.Sum();
    }
}
