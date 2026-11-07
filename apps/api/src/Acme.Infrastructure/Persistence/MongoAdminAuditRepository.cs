using System.Reflection;
using Acme.Application.Admin;
using Acme.Domain.Admin;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoAdminAuditRepository : IAdminAuditRepository
{
    private const string CollectionName = "admin_audit";
    private readonly IMongoCollection<AdminAuditDocument> _collection;

    public MongoAdminAuditRepository(MongoContext ctx)
    {
        _collection = ctx.Collection<AdminAuditDocument>(CollectionName);
    }

    public Task AddAsync(AdminAuditEntry entry, CancellationToken ct) =>
        _collection.InsertOneAsync(
            new AdminAuditDocument
            {
                Id = entry.Id,
                ActorId = entry.ActorId,
                ActorEmail = entry.ActorEmail,
                Action = (int)entry.Action,
                TargetUserId = entry.TargetUserId,
                TargetEmail = entry.TargetEmail,
                Detail = entry.Detail,
                CreatedAtUtc = entry.CreatedAtUtc,
            },
            cancellationToken: ct);

    public async Task<AuditPage> SearchAsync(AuditQuery query, CancellationToken ct)
    {
        var filter = BuildFilter(query);

        var totalTask = _collection.CountDocumentsAsync(filter, cancellationToken: ct);
        var itemsTask = _collection
            .Find(filter)
            .SortByDescending(x => x.CreatedAtUtc)
            .Skip(Math.Max(0, query.Skip))
            .Limit(Math.Clamp(query.Take, 1, 200))
            .ToListAsync(ct);

        await Task.WhenAll(totalTask, itemsTask);

        return new AuditPage(itemsTask.Result.Select(ToDomain).ToList(), totalTask.Result);
    }

    private static FilterDefinition<AdminAuditDocument> BuildFilter(AuditQuery q)
    {
        var f = Builders<AdminAuditDocument>.Filter;
        var clauses = new List<FilterDefinition<AdminAuditDocument>>();

        if (!string.IsNullOrWhiteSpace(q.ActorId))
            clauses.Add(f.Eq(x => x.ActorId, q.ActorId));

        if (q.Action is { } action)
            clauses.Add(f.Eq(x => x.Action, (int)action));

        if (q.FromUtc is { } from)
            clauses.Add(f.Gte(x => x.CreatedAtUtc, from));

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            // Escaped so a search containing regex metacharacters is treated
            // as a literal rather than as a pattern.
            var term = System.Text.RegularExpressions.Regex.Escape(q.Search.Trim());
            var rx = new MongoDB.Bson.BsonRegularExpression(term, "i");
            clauses.Add(f.Or(
                f.Regex(x => x.ActorEmail, rx),
                f.Regex(x => x.TargetEmail, rx),
                f.Regex(x => x.Detail, rx)));
        }

        return clauses.Count == 0 ? f.Empty : f.And(clauses);
    }

    // The entity is intentionally immutable with no public constructor, so
    // rehydration goes through reflection — the same approach UserMapper uses.
    private static readonly ConstructorInfo PrivateCtor =
        typeof(AdminAuditEntry).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("AdminAuditEntry parameterless ctor missing.");

    private static AdminAuditEntry ToDomain(AdminAuditDocument d)
    {
        var e = (AdminAuditEntry)PrivateCtor.Invoke(null);
        Set(e, nameof(AdminAuditEntry.Id), d.Id);
        Set(e, nameof(AdminAuditEntry.ActorId), d.ActorId);
        Set(e, nameof(AdminAuditEntry.ActorEmail), d.ActorEmail);
        Set(e, nameof(AdminAuditEntry.Action), (AdminAction)d.Action);
        Set(e, nameof(AdminAuditEntry.TargetUserId), d.TargetUserId);
        Set(e, nameof(AdminAuditEntry.TargetEmail), d.TargetEmail);
        Set(e, nameof(AdminAuditEntry.Detail), d.Detail);
        Set(e, nameof(AdminAuditEntry.CreatedAtUtc), d.CreatedAtUtc);
        Set(e, nameof(AdminAuditEntry.UpdatedAtUtc), d.CreatedAtUtc);
        return e;
    }

    private static void Set(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(
            name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}

internal sealed class AdminAuditDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("actorId")] public string ActorId { get; set; } = default!;
    [BsonElement("actorEmail")] public string ActorEmail { get; set; } = default!;
    [BsonElement("action")] public int Action { get; set; }
    [BsonElement("targetUserId")] public string? TargetUserId { get; set; }
    [BsonElement("targetEmail")] public string? TargetEmail { get; set; }
    [BsonElement("detail")] public string? Detail { get; set; }
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
}
