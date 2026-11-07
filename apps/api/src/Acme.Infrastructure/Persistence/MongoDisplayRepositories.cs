using Acme.Application.Displays;
using Acme.Domain.Displays;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoDisplayScreenRepository : IDisplayScreenRepository
{
    internal const string CollectionName = "display_screens";

    private readonly IMongoCollection<DisplayScreenDocument> _collection;

    public MongoDisplayScreenRepository(MongoContext ctx)
    {
        // Indexes are declared centrally in MongoIndexInitializer.
        _collection = ctx.Collection<DisplayScreenDocument>(CollectionName);
    }

    public async Task<IReadOnlyList<DisplayScreen>> ListByOwnerAsync(
        string ownerId, CancellationToken ct)
    {
        var docs = await _collection
            .Find(x => x.OwnerId == ownerId)
            .SortBy(x => x.Name)
            .ToListAsync(ct);

        return docs.Select(DisplayMapper.Hydrate).ToList();
    }

    public async Task<int> CountByOwnerAsync(string ownerId, CancellationToken ct) =>
        (int)await _collection.CountDocumentsAsync(x => x.OwnerId == ownerId, cancellationToken: ct);

    public async Task<DisplayScreen?> FindByIdAsync(string id, CancellationToken ct)
    {
        var doc = await _collection.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc is null ? null : DisplayMapper.Hydrate(doc);
    }

    /// <summary>
    /// The kiosk's hot path: one screen, by the hash of the token it presented.
    /// </summary>
    /// <remarks>
    /// <b>Revoked credentials are excluded in the query, not afterwards.</b>
    /// Matching on the hash alone and filtering in memory would work today and
    /// would quietly stop working the moment somebody reused this method — and
    /// the failure would be a revoked screen that kept showing content.
    /// </remarks>
    public async Task<DisplayScreen?> FindByCredentialHashAsync(
        string tokenHash, CancellationToken ct)
    {
        var filter = Builders<DisplayScreenDocument>.Filter.ElemMatch(
            x => x.Credentials,
            Builders<DisplayCredentialDocument>.Filter.And(
                Builders<DisplayCredentialDocument>.Filter.Eq(c => c.TokenHash, tokenHash),
                Builders<DisplayCredentialDocument>.Filter.Eq(c => c.RevokedAtUtc, null)));

        var doc = await _collection.Find(filter).FirstOrDefaultAsync(ct);
        return doc is null ? null : DisplayMapper.Hydrate(doc);
    }

    public async Task<DisplayScreen?> FindByPairingCodeAsync(string code, CancellationToken ct)
    {
        var doc = await _collection.Find(x => x.PairingCode == code).FirstOrDefaultAsync(ct);
        return doc is null ? null : DisplayMapper.Hydrate(doc);
    }

    public Task AddAsync(DisplayScreen screen, CancellationToken ct) =>
        Translating(() =>
            _collection.InsertOneAsync(DisplayMapper.ToDocument(screen), cancellationToken: ct));

    public Task UpdateAsync(DisplayScreen screen, CancellationToken ct) =>
        Translating(() =>
            _collection.ReplaceOneAsync(
                x => x.Id == screen.Id, DisplayMapper.ToDocument(screen), cancellationToken: ct));

    /// <summary>
    /// Turns a duplicate-key error into something the service can act on.
    /// </summary>
    /// <remarks>
    /// <c>uniq_pairing_code</c> is the only unique index on this collection, so
    /// a duplicate key here can mean nothing else. Checking the category rather
    /// than the message text, because the message is a server-formatted string
    /// that has changed between MongoDB versions before.
    /// </remarks>
    private static async Task Translating(Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (MongoWriteException ex)
            when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicatePairingCodeException();
        }
    }

    public Task DeleteAsync(string id, CancellationToken ct) =>
        _collection.DeleteOneAsync(x => x.Id == id, ct);

    /// <summary>
    /// Writes two timestamps and nothing else.
    /// </summary>
    /// <remarks>
    /// A targeted <c>$set</c> rather than a replace. Every screen calls this on
    /// every poll for the lifetime of the installation; replacing the document
    /// would rewrite the credential list thirty times a minute across an estate
    /// and would race with a revoke happening in the dashboard — the poll would
    /// cheerfully write the pre-revocation credentials back.
    /// </remarks>
    public Task TouchLastSeenAsync(
        string screenId, string credentialId, DateTime seenAtUtc, CancellationToken ct)
    {
        var filter = Builders<DisplayScreenDocument>.Filter.And(
            Builders<DisplayScreenDocument>.Filter.Eq(x => x.Id, screenId),
            Builders<DisplayScreenDocument>.Filter.ElemMatch(
                x => x.Credentials,
                Builders<DisplayCredentialDocument>.Filter.Eq(c => c.Id, credentialId)));

        var update = Builders<DisplayScreenDocument>.Update
            .Set(x => x.LastSeenAtUtc, seenAtUtc)
            .Set("credentials.$.lastSeenAtUtc", seenAtUtc);

        return _collection.UpdateOneAsync(filter, update, cancellationToken: ct);
    }
}

public sealed class MongoPlaylistRepository : IPlaylistRepository
{
    internal const string CollectionName = "display_playlists";

    private readonly IMongoCollection<PlaylistDocument> _collection;

    public MongoPlaylistRepository(MongoContext ctx)
    {
        _collection = ctx.Collection<PlaylistDocument>(CollectionName);
    }

    public async Task<IReadOnlyList<Playlist>> ListByOwnerAsync(
        string ownerId, CancellationToken ct)
    {
        var docs = await _collection
            .Find(x => x.OwnerId == ownerId)
            .SortBy(x => x.Name)
            .ToListAsync(ct);

        return docs.Select(DisplayMapper.Hydrate).ToList();
    }

    public async Task<Playlist?> FindByIdAsync(string id, CancellationToken ct)
    {
        var doc = await _collection.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc is null ? null : DisplayMapper.Hydrate(doc);
    }

    public Task AddAsync(Playlist playlist, CancellationToken ct) =>
        _collection.InsertOneAsync(DisplayMapper.ToDocument(playlist), cancellationToken: ct);

    public Task UpdateAsync(Playlist playlist, CancellationToken ct) =>
        _collection.ReplaceOneAsync(
            x => x.Id == playlist.Id, DisplayMapper.ToDocument(playlist), cancellationToken: ct);

    public Task DeleteAsync(string id, CancellationToken ct) =>
        _collection.DeleteOneAsync(x => x.Id == id, ct);
}
