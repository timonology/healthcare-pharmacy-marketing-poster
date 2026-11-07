using System.Reflection;
using Acme.Application.Invitations;
using Acme.Domain.Users;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

internal sealed class PharmacyInvitationDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("userId")] public string UserId { get; set; } = default!;

    /// <summary>SHA-256 hex. The raw token is never stored — see PharmacyInvitation.</summary>
    [BsonElement("tokenHash")] public string TokenHash { get; set; } = default!;

    [BsonElement("expiresAtUtc")] public DateTime ExpiresAtUtc { get; set; }
    [BsonElement("acceptedAtUtc")] public DateTime? AcceptedAtUtc { get; set; }

    [BsonElement("isSuperseded")]
    [BsonDefaultValue(false)]
    public bool IsSuperseded { get; set; }

    [BsonElement("invitedByEmail")] public string InvitedByEmail { get; set; } = default!;
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}

public sealed class MongoPharmacyInvitationRepository(MongoContext ctx) : IPharmacyInvitationRepository
{
    private IMongoCollection<PharmacyInvitationDocument> Col =>
        ctx.Collection<PharmacyInvitationDocument>("pharmacy_invitations");

    private static readonly ConstructorInfo Ctor =
        typeof(PharmacyInvitation).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("PharmacyInvitation parameterless ctor missing.");

    public Task AddAsync(PharmacyInvitation invitation, CancellationToken ct) =>
        Col.InsertOneAsync(ToDocument(invitation), cancellationToken: ct);

    public Task UpdateAsync(PharmacyInvitation invitation, CancellationToken ct) =>
        Col.ReplaceOneAsync(
            x => x.Id == invitation.Id, ToDocument(invitation),
            new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<PharmacyInvitation?> FindByTokenHashAsync(string tokenHash, CancellationToken ct)
    {
        var doc = await Col.Find(x => x.TokenHash == tokenHash).FirstOrDefaultAsync(ct);
        return doc is null ? null : Hydrate(doc);
    }

    public async Task<IReadOnlyList<PharmacyInvitation>> ListForUserAsync(
        string userId, CancellationToken ct)
    {
        var docs = await Col.Find(x => x.UserId == userId)
            .SortByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        return docs.Select(Hydrate).ToList();
    }

    /// <summary>
    /// The newest invitation per account, in one query.
    /// </summary>
    /// <remarks>
    /// Sorted newest-first and kept on first sight, rather than aggregating.
    /// The set is one page of accounts, so the in-memory pass is trivial and
    /// avoids a $group pipeline that Cosmos's Mongo layer handles unevenly.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, PharmacyInvitation>> LatestForUsersAsync(
        IReadOnlyList<string> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
            return new Dictionary<string, PharmacyInvitation>();

        var docs = await Col
            .Find(Builders<PharmacyInvitationDocument>.Filter.In(x => x.UserId, userIds))
            .SortByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        var latest = new Dictionary<string, PharmacyInvitation>();
        foreach (var doc in docs)
            if (!latest.ContainsKey(doc.UserId))
                latest[doc.UserId] = Hydrate(doc);

        return latest;
    }

    private static PharmacyInvitationDocument ToDocument(PharmacyInvitation i) => new()
    {
        Id = i.Id,
        UserId = i.UserId,
        TokenHash = i.TokenHash,
        ExpiresAtUtc = i.ExpiresAtUtc,
        AcceptedAtUtc = i.AcceptedAtUtc,
        IsSuperseded = i.IsSuperseded,
        InvitedByEmail = i.InvitedByEmail,
        CreatedAtUtc = i.CreatedAtUtc,
        UpdatedAtUtc = i.UpdatedAtUtc,
    };

    private static PharmacyInvitation Hydrate(PharmacyInvitationDocument d)
    {
        var i = (PharmacyInvitation)Ctor.Invoke(null);
        Set(i, nameof(PharmacyInvitation.Id), d.Id);
        Set(i, nameof(PharmacyInvitation.UserId), d.UserId);
        Set(i, nameof(PharmacyInvitation.TokenHash), d.TokenHash);
        Set(i, nameof(PharmacyInvitation.ExpiresAtUtc), d.ExpiresAtUtc);
        Set(i, nameof(PharmacyInvitation.AcceptedAtUtc), d.AcceptedAtUtc);
        Set(i, nameof(PharmacyInvitation.IsSuperseded), d.IsSuperseded);
        Set(i, nameof(PharmacyInvitation.InvitedByEmail), d.InvitedByEmail);
        Set(i, nameof(PharmacyInvitation.CreatedAtUtc), d.CreatedAtUtc);
        Set(i, nameof(PharmacyInvitation.UpdatedAtUtc), d.UpdatedAtUtc);
        return i;
    }

    private static void Set(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(
            name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}

/// <summary>Builds the activation URL from the configured public web address.</summary>
public sealed class ConfiguredInvitationLinkBuilder(
    Microsoft.Extensions.Configuration.IConfiguration configuration) : IInvitationLinkBuilder
{
    public string BuildActivationLink(string rawToken)
    {
        // Falls back to the CORS origin, which is already the web app's public
        // address — one fewer setting to forget on a new environment, and it
        // cannot silently point somewhere else.
        var baseUrl =
            configuration["Public:WebBaseUrl"]
            ?? configuration["Cors:Origins:0"]
            ?? "http://localhost:3000";

        return $"{baseUrl.TrimEnd('/')}/activate?token={Uri.EscapeDataString(rawToken)}";
    }
}
