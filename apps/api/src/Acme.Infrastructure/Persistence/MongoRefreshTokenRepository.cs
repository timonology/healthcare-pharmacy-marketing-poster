using Acme.Application.Auth;
using Acme.Domain.Auth;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoRefreshTokenRepository : IRefreshTokenRepository
{
    private const string CollectionName = "refresh_tokens";
    private readonly IMongoCollection<RefreshTokenDocument> _collection;

    public MongoRefreshTokenRepository(MongoContext ctx)
    {
        // Indexes are declared centrally in MongoIndexInitializer.
        _collection = ctx.Collection<RefreshTokenDocument>(CollectionName);
    }

    public Task AddAsync(RefreshToken token, CancellationToken ct) =>
        _collection.InsertOneAsync(RefreshTokenMapper.ToDocument(token), cancellationToken: ct);

    public async Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct)
    {
        var doc = await _collection
            .Find(x => x.TokenHash == tokenHash)
            .FirstOrDefaultAsync(ct);
        return doc is null ? null : RefreshTokenMapper.Hydrate(doc);
    }

    public Task UpdateAsync(RefreshToken token, CancellationToken ct) =>
        _collection.ReplaceOneAsync(
            x => x.Id == token.Id,
            RefreshTokenMapper.ToDocument(token),
            cancellationToken: ct);

    public async Task RevokeAllForUserAsync(string userId, CancellationToken ct)
    {
        var update = Builders<RefreshTokenDocument>.Update
            .Set(x => x.RevokedAtUtc, DateTime.UtcNow)
            .Set(x => x.UpdatedAtUtc, DateTime.UtcNow);

        await _collection.UpdateManyAsync(
            x => x.UserId == userId && x.RevokedAtUtc == null,
            update,
            cancellationToken: ct);
    }
}
