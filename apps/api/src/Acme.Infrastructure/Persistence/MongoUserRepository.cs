using Acme.Application.Auth;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoUserRepository : IUserRepository
{
    private const string CollectionName = "users";
    private readonly IMongoCollection<UserDocument> _collection;

    public MongoUserRepository(MongoContext ctx)
    {
        _collection = ctx.Collection<UserDocument>(CollectionName);
    }

    public async Task<User?> FindByEmailAsync(Email email, CancellationToken ct)
    {
        var doc = await _collection
            .Find(x => x.Email == email.Value)
            .FirstOrDefaultAsync(ct);
        return doc is null ? null : ToDomain(doc);
    }

    public async Task<User?> FindByIdAsync(string id, CancellationToken ct)
    {
        var doc = await _collection.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc is null ? null : ToDomain(doc);
    }

    public Task AddAsync(User user, CancellationToken ct) =>
        _collection.InsertOneAsync(ToDocument(user), cancellationToken: ct);

    public Task UpdateAsync(User user, CancellationToken ct) =>
        _collection.ReplaceOneAsync(
            x => x.Id == user.Id,
            ToDocument(user),
            options: new ReplaceOptions { IsUpsert = false },
            cancellationToken: ct);

    private static UserDocument ToDocument(User u) => new()
    {
        Id = u.Id,
        Email = u.Email.Value,
        DisplayName = u.DisplayName,
        PasswordHash = u.PasswordHash.Value,
        Tier = (int)u.Tier,
        CreatedAtUtc = u.CreatedAtUtc,
        UpdatedAtUtc = u.UpdatedAtUtc,
    };

    private static User ToDomain(UserDocument d) =>
        UserMapper.Hydrate(
            d.Id,
            d.Email,
            d.DisplayName,
            d.PasswordHash,
            (SubscriptionTier)d.Tier,
            d.CreatedAtUtc,
            d.UpdatedAtUtc);
}
