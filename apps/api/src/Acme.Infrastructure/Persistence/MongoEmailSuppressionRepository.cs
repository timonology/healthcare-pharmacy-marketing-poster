using Acme.Application.Patients;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

internal sealed class EmailSuppressionDocument
{
    /// <summary>"{ownerId}|{email}" — one row per pharmacy and address, by construction.</summary>
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("ownerId")] public string OwnerId { get; set; } = default!;
    [BsonElement("email")] public string Email { get; set; } = default!;
    [BsonElement("source")] public string Source { get; set; } = default!;
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
}

/// <summary>The suppression list (security review R2-06), collection <c>email_suppressions</c>.</summary>
public sealed class MongoEmailSuppressionRepository(MongoContext ctx) : IEmailSuppressionRepository
{
    internal const string CollectionName = "email_suppressions";

    private IMongoCollection<EmailSuppressionDocument> Col =>
        ctx.Collection<EmailSuppressionDocument>(CollectionName);

    private static string Key(string ownerId, string email) => $"{ownerId}|{UnsubscribeToken.Normalise(email)}";

    public async Task AddAsync(string ownerId, string email, string source, CancellationToken ct)
    {
        var normalised = UnsubscribeToken.Normalise(email);
        var id = Key(ownerId, normalised);
        // Upsert on the composite id: a second click is a no-op, and the first
        // date is the one kept.
        try
        {
            await Col.UpdateOneAsync(
                Builders<EmailSuppressionDocument>.Filter.Eq(x => x.Id, id),
                Builders<EmailSuppressionDocument>.Update
                    .SetOnInsert(x => x.OwnerId, ownerId)
                    .SetOnInsert(x => x.Email, normalised)
                    .SetOnInsert(x => x.Source, source)
                    .SetOnInsert(x => x.CreatedAtUtc, DateTime.UtcNow),
                new UpdateOptions { IsUpsert = true },
                ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Two upserts racing — the mail client's one-click POST and the
            // page's button at the same moment. The other one inserted the
            // row; this one has nothing left to do.
        }
    }

    public async Task<bool> IsSuppressedAsync(string ownerId, string email, CancellationToken ct)
    {
        var id = Key(ownerId, email);
        return await Col.Find(Builders<EmailSuppressionDocument>.Filter.Eq(x => x.Id, id)).AnyAsync(ct);
    }

    public async Task<IReadOnlySet<string>> FindSuppressedAsync(
        string ownerId, IReadOnlyCollection<string> emails, CancellationToken ct)
    {
        if (emails.Count == 0) return new HashSet<string>();
        var ids = emails.Select(e => Key(ownerId, e)).Distinct().ToList();
        var found = await Col.Find(Builders<EmailSuppressionDocument>.Filter.In(x => x.Id, ids))
            .Project(x => x.Email)
            .ToListAsync(ct);
        return found.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
