using System.Reflection;
using Acme.Application.Hierarchy;
using Acme.Domain.Users;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

internal sealed class PharmacyMembershipDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("userId")] public string UserId { get; set; } = default!;
    [BsonElement("pharmacyId")] public string PharmacyId { get; set; } = default!;
    [BsonElement("role")] public int Role { get; set; }
    [BsonElement("grantedBy")] public string GrantedBy { get; set; } = default!;
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}

public sealed class MongoPharmacyMembershipRepository(MongoContext ctx) : IPharmacyMembershipRepository
{
    private IMongoCollection<PharmacyMembershipDocument> Col =>
        ctx.Collection<PharmacyMembershipDocument>("pharmacy_memberships");

    private static readonly ConstructorInfo Ctor =
        typeof(PharmacyMembership).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("PharmacyMembership parameterless ctor missing.");

    public async Task<IReadOnlyList<PharmacyMembership>> ListForUserAsync(
        string userId, CancellationToken ct)
    {
        var docs = await Col.Find(x => x.UserId == userId)
            .SortBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        return docs.Select(Hydrate).ToList();
    }

    public async Task<IReadOnlyList<PharmacyMembership>> ListForPharmacyAsync(
        string pharmacyId, CancellationToken ct)
    {
        var docs = await Col.Find(x => x.PharmacyId == pharmacyId)
            .SortBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        return docs.Select(Hydrate).ToList();
    }

    public async Task<PharmacyMembership?> FindAsync(
        string userId, string pharmacyId, CancellationToken ct)
    {
        var doc = await Col
            .Find(x => x.UserId == userId && x.PharmacyId == pharmacyId)
            .FirstOrDefaultAsync(ct);
        return doc is null ? null : Hydrate(doc);
    }

    public Task AddAsync(PharmacyMembership membership, CancellationToken ct) =>
        Col.InsertOneAsync(ToDocument(membership), cancellationToken: ct);

    public Task AddManyAsync(IEnumerable<PharmacyMembership> memberships, CancellationToken ct)
    {
        var docs = memberships.Select(ToDocument).ToList();
        return docs.Count == 0
            ? Task.CompletedTask
            // Unordered so one duplicate does not abandon the rest — the backfill
            // races the unique index by design and expects some rows to bounce.
            : Col.InsertManyAsync(docs, new InsertManyOptions { IsOrdered = false }, ct);
    }

    public Task UpdateAsync(PharmacyMembership membership, CancellationToken ct) =>
        Col.ReplaceOneAsync(
            x => x.Id == membership.Id, ToDocument(membership),
            new ReplaceOptions { IsUpsert = true }, ct);

    public Task RemoveAsync(string id, CancellationToken ct) =>
        Col.DeleteOneAsync(x => x.Id == id, ct);

    public Task<long> CountAsync(CancellationToken ct) =>
        Col.CountDocumentsAsync(FilterDefinition<PharmacyMembershipDocument>.Empty, cancellationToken: ct);

    private static PharmacyMembershipDocument ToDocument(PharmacyMembership m) => new()
    {
        Id = m.Id,
        UserId = m.UserId,
        PharmacyId = m.PharmacyId,
        Role = (int)m.Role,
        GrantedBy = m.GrantedBy,
        CreatedAtUtc = m.CreatedAtUtc,
        UpdatedAtUtc = m.UpdatedAtUtc,
    };

    private static PharmacyMembership Hydrate(PharmacyMembershipDocument d)
    {
        var m = (PharmacyMembership)Ctor.Invoke(null);
        Set(m, nameof(PharmacyMembership.Id), d.Id);
        Set(m, nameof(PharmacyMembership.UserId), d.UserId);
        Set(m, nameof(PharmacyMembership.PharmacyId), d.PharmacyId);
        Set(m, nameof(PharmacyMembership.Role), (PharmacyMembershipRole)d.Role);
        Set(m, nameof(PharmacyMembership.GrantedBy), d.GrantedBy);
        Set(m, nameof(PharmacyMembership.CreatedAtUtc), d.CreatedAtUtc);
        Set(m, nameof(PharmacyMembership.UpdatedAtUtc), d.UpdatedAtUtc);
        return m;
    }

    private static void Set(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(
            name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}
