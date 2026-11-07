using System.Reflection;
using Acme.Application.Hierarchy;
using Acme.Domain.Users;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

internal sealed class PharmacyAccessRequestDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("userId")] public string UserId { get; set; } = default!;
    [BsonElement("userEmail")] public string UserEmail { get; set; } = default!;
    [BsonElement("pharmacyId")] public string PharmacyId { get; set; } = default!;
    [BsonElement("pharmacyEmail")] public string PharmacyEmail { get; set; } = default!;
    [BsonElement("status")] public int Status { get; set; }
    [BsonElement("reason")] public string Reason { get; set; } = default!;
    [BsonElement("reviewedByUserId")] public string? ReviewedByUserId { get; set; }
    [BsonElement("reviewedByEmail")] public string? ReviewedByEmail { get; set; }
    [BsonElement("reviewedAtUtc")] public DateTime? ReviewedAtUtc { get; set; }
    [BsonElement("reviewNote")] public string? ReviewNote { get; set; }
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}

public sealed class MongoPharmacyAccessRequestRepository(MongoContext ctx)
    : IPharmacyAccessRequestRepository
{
    private IMongoCollection<PharmacyAccessRequestDocument> Col =>
        ctx.Collection<PharmacyAccessRequestDocument>("pharmacy_access_requests");

    private static readonly ConstructorInfo Ctor =
        typeof(PharmacyAccessRequest).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("PharmacyAccessRequest parameterless ctor missing.");

    public Task AddAsync(PharmacyAccessRequest request, CancellationToken ct) =>
        Col.InsertOneAsync(ToDocument(request), cancellationToken: ct);

    public Task UpdateAsync(PharmacyAccessRequest request, CancellationToken ct) =>
        Col.ReplaceOneAsync(
            x => x.Id == request.Id, ToDocument(request),
            new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<PharmacyAccessRequest?> FindByIdAsync(string id, CancellationToken ct)
    {
        var doc = await Col.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc is null ? null : Hydrate(doc);
    }

    public async Task<IReadOnlyList<PharmacyAccessRequest>> ListForUserAsync(
        string userId, CancellationToken ct)
    {
        var docs = await Col.Find(x => x.UserId == userId)
            .SortByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        return docs.Select(Hydrate).ToList();
    }

    public async Task<PharmacyAccessRequest?> FindPendingAsync(
        string userId, string pharmacyId, CancellationToken ct)
    {
        var doc = await Col
            .Find(x => x.UserId == userId
                    && x.PharmacyId == pharmacyId
                    && x.Status == (int)AccessRequestStatus.Pending)
            .FirstOrDefaultAsync(ct);
        return doc is null ? null : Hydrate(doc);
    }

    public async Task<IReadOnlyList<PharmacyAccessRequest>> ListForReviewAsync(
        bool pendingOnly, int take, CancellationToken ct)
    {
        var filter = pendingOnly
            ? Builders<PharmacyAccessRequestDocument>.Filter.Eq(
                x => x.Status, (int)AccessRequestStatus.Pending)
            : Builders<PharmacyAccessRequestDocument>.Filter.Empty;

        // Pending first, then newest — so the queue opens on the things that
        // need a decision rather than on a history of ones that do not.
        var docs = await Col.Find(filter)
            .SortBy(x => x.Status)
            .ThenByDescending(x => x.CreatedAtUtc)
            .Limit(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);

        return docs.Select(Hydrate).ToList();
    }

    private static PharmacyAccessRequestDocument ToDocument(PharmacyAccessRequest r) => new()
    {
        Id = r.Id,
        UserId = r.UserId,
        UserEmail = r.UserEmail,
        PharmacyId = r.PharmacyId,
        PharmacyEmail = r.PharmacyEmail,
        Status = (int)r.Status,
        Reason = r.Reason,
        ReviewedByUserId = r.ReviewedByUserId,
        ReviewedByEmail = r.ReviewedByEmail,
        ReviewedAtUtc = r.ReviewedAtUtc,
        ReviewNote = r.ReviewNote,
        CreatedAtUtc = r.CreatedAtUtc,
        UpdatedAtUtc = r.UpdatedAtUtc,
    };

    private static PharmacyAccessRequest Hydrate(PharmacyAccessRequestDocument d)
    {
        var r = (PharmacyAccessRequest)Ctor.Invoke(null);
        Set(r, nameof(PharmacyAccessRequest.Id), d.Id);
        Set(r, nameof(PharmacyAccessRequest.UserId), d.UserId);
        Set(r, nameof(PharmacyAccessRequest.UserEmail), d.UserEmail);
        Set(r, nameof(PharmacyAccessRequest.PharmacyId), d.PharmacyId);
        Set(r, nameof(PharmacyAccessRequest.PharmacyEmail), d.PharmacyEmail);
        Set(r, nameof(PharmacyAccessRequest.Status), (AccessRequestStatus)d.Status);
        Set(r, nameof(PharmacyAccessRequest.Reason), d.Reason);
        Set(r, nameof(PharmacyAccessRequest.ReviewedByUserId), d.ReviewedByUserId);
        Set(r, nameof(PharmacyAccessRequest.ReviewedByEmail), d.ReviewedByEmail);
        Set(r, nameof(PharmacyAccessRequest.ReviewedAtUtc), d.ReviewedAtUtc);
        Set(r, nameof(PharmacyAccessRequest.ReviewNote), d.ReviewNote);
        Set(r, nameof(PharmacyAccessRequest.CreatedAtUtc), d.CreatedAtUtc);
        Set(r, nameof(PharmacyAccessRequest.UpdatedAtUtc), d.UpdatedAtUtc);
        return r;
    }

    private static void Set(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(
            name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}
