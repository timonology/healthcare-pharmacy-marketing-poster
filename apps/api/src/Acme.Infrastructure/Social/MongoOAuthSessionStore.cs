using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Acme.Application.Social;
using Acme.Domain.Social;
using Acme.Infrastructure.Persistence;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Acme.Infrastructure.Social;

/// <summary>
/// OAuth flows in Mongo, so Start, the callback and the account choice can each
/// land on a different API instance.
/// </summary>
/// <remarks>
/// <para>
/// This replaced an in-memory cache, which worked on one instance and failed at
/// random on two: the callback reached one instance and the "connect these"
/// call another, which had never heard of the session.
/// </para>
/// <para>
/// <b>What is stored, and for how long.</b> The document is keyed by a SHA-256
/// of the state, not the state itself, so read access to the database does not
/// yield a state that could be redeemed. Discovered accounts — which carry live
/// platform tokens — are serialised and encrypted with the same AES-GCM cipher
/// as connected accounts' credentials, for at most the flow's ten minutes, and
/// the document is deleted the moment they are taken. A TTL index removes any
/// flow nobody finished; every read also checks expiry, because the TTL
/// monitor only runs once a minute.
/// </para>
/// <para>
/// Each transition is one atomic findAndModify, which is what makes a state
/// and a set of discovered accounts single-use across instances.
/// </para>
/// </remarks>
public sealed class MongoOAuthSessionStore(MongoContext ctx, ITokenCipher cipher) : IOAuthSessionStore
{
    public const string CollectionName = "oauth_sessions";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IMongoCollection<OAuthSessionDocument> Col =>
        ctx.Collection<OAuthSessionDocument>(CollectionName);

    public Task BeginAsync(string state, OAuthFlow flow, TimeSpan ttl, CancellationToken ct) =>
        Col.InsertOneAsync(new OAuthSessionDocument
        {
            Id = KeyFor(state),
            OwnerId = flow.OwnerId,
            Kind = (int)flow.Kind,
            RedirectUri = flow.RedirectUri,
            Phase = OAuthSessionPhase.Started,
            ExpiresAtUtc = DateTime.UtcNow.Add(ttl),
        }, cancellationToken: ct);

    public async Task<OAuthFlow?> TakeStartedAsync(
        string state, string ownerId, SocialOAuthProviderKind kind, CancellationToken ct)
    {
        var fb = Builders<OAuthSessionDocument>.Filter;
        var filter = fb.Eq(x => x.Id, KeyFor(state))
                   & fb.Eq(x => x.OwnerId, ownerId)
                   & fb.Eq(x => x.Kind, (int)kind)
                   & fb.Eq(x => x.Phase, OAuthSessionPhase.Started)
                   & fb.Gt(x => x.ExpiresAtUtc, DateTime.UtcNow);

        // Moved on, not deleted: the same document receives the discovered
        // accounts next, and a second callback with this state now finds
        // nothing in the Started phase.
        var doc = await Col.FindOneAndUpdateAsync(
            filter,
            Builders<OAuthSessionDocument>.Update.Set(x => x.Phase, OAuthSessionPhase.Exchanging),
            new FindOneAndUpdateOptions<OAuthSessionDocument> { ReturnDocument = ReturnDocument.After },
            ct);

        return doc is null ? null : new OAuthFlow(doc.OwnerId, (SocialOAuthProviderKind)doc.Kind, doc.RedirectUri);
    }

    public async Task SaveDiscoveredAsync(string state, OAuthFlow flow, TimeSpan ttl, CancellationToken ct)
    {
        var accounts = flow.Accounts ?? Array.Empty<DiscoveredAccount>();
        var secret = cipher.Encrypt(JsonSerializer.Serialize(accounts, Json));

        var fb = Builders<OAuthSessionDocument>.Filter;
        var filter = fb.Eq(x => x.Id, KeyFor(state))
                   & fb.Eq(x => x.OwnerId, flow.OwnerId)
                   & fb.Eq(x => x.Phase, OAuthSessionPhase.Exchanging);

        var update = Builders<OAuthSessionDocument>.Update
            .Set(x => x.Phase, OAuthSessionPhase.Discovered)
            .Set(x => x.Accounts, new EncryptedBlob
            {
                CipherText = secret.CipherText,
                Nonce = secret.Nonce,
                Tag = secret.Tag,
                KeyVersion = secret.KeyVersion,
            })
            .Set(x => x.ExpiresAtUtc, DateTime.UtcNow.Add(ttl));

        await Col.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    public async Task<OAuthFlow?> TakeDiscoveredAsync(string state, string ownerId, CancellationToken ct)
    {
        var fb = Builders<OAuthSessionDocument>.Filter;
        var filter = fb.Eq(x => x.Id, KeyFor(state))
                   & fb.Eq(x => x.OwnerId, ownerId)
                   & fb.Eq(x => x.Phase, OAuthSessionPhase.Discovered)
                   & fb.Gt(x => x.ExpiresAtUtc, DateTime.UtcNow);

        var doc = await Col.FindOneAndDeleteAsync(filter, cancellationToken: ct);
        if (doc?.Accounts is not { } blob) return null;

        var json = cipher.Decrypt(
            EncryptedSecret.Create(blob.CipherText, blob.Nonce, blob.Tag, blob.KeyVersion));
        var accounts = JsonSerializer.Deserialize<List<DiscoveredAccount>>(json, Json)
                       ?? new List<DiscoveredAccount>();

        return new OAuthFlow(doc.OwnerId, (SocialOAuthProviderKind)doc.Kind, doc.RedirectUri, accounts);
    }

    /// <summary>The stored key: a hash of the state, never the state itself.</summary>
    public static string KeyFor(string state) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state ?? string.Empty))).ToLowerInvariant();
}

internal static class OAuthSessionPhase
{
    public const int Started = 0;
    public const int Exchanging = 1;
    public const int Discovered = 2;
}

internal sealed class OAuthSessionDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("ownerId")] public string OwnerId { get; set; } = default!;
    [BsonElement("kind")] public int Kind { get; set; }
    [BsonElement("redirectUri")] public string RedirectUri { get; set; } = default!;
    [BsonElement("phase")] public int Phase { get; set; }
    [BsonElement("accounts")] public EncryptedBlob? Accounts { get; set; }

    /// <summary>TTL-indexed: Mongo deletes the document once this passes.</summary>
    [BsonElement("expiresAtUtc")] public DateTime ExpiresAtUtc { get; set; }
}

internal sealed class EncryptedBlob
{
    [BsonElement("cipherText")] public string CipherText { get; set; } = default!;
    [BsonElement("nonce")] public string Nonce { get; set; } = default!;
    [BsonElement("tag")] public string Tag { get; set; } = default!;
    [BsonElement("keyVersion")] public int KeyVersion { get; set; }
}
