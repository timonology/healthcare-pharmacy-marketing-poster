using System.Text.RegularExpressions;
using Acme.Application.Audit;
using Acme.Application.Common;
using Acme.Infrastructure.Persistence;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Acme.Infrastructure.Audit;

/// <summary>
/// <c>audit_events</c> and <c>activity_events</c>, in the application's own
/// database. Two collections of one shape: see <see cref="AuditEvent"/>.
/// </summary>
public sealed class MongoAuditEventStore(MongoContext ctx) : IAuditEventStore
{
    public const string AuditCollection = "audit_events";
    public const string ActivityCollection = "activity_events";

    /// <summary>Hard ceiling on one page, so a crafted request cannot ask for the whole log.</summary>
    public const int MaxTake = 10_000;

    /// <summary>Totals stop counting here.</summary>
    public const long CountCap = 100_000;

    internal IMongoCollection<AuditEventDocument> Collection(AuditStream stream) =>
        ctx.Collection<AuditEventDocument>(stream == AuditStream.Audit ? AuditCollection : ActivityCollection);

    public Task InsertAsync(AuditStream stream, AuditEvent e, CancellationToken ct) =>
        Collection(stream).InsertOneAsync(AuditEventDocument.From(e), cancellationToken: ct);

    public async Task<bool> InsertIfMissingAsync(AuditStream stream, AuditEvent e, CancellationToken ct)
    {
        // A plain insert, so an existing row is never touched — an audit row,
        // once written, is not rewritten even by the backfill that wrote it.
        try
        {
            await Collection(stream).InsertOneAsync(AuditEventDocument.From(e), cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<bool> ExistsSinceAsync(
        AuditStream stream, string actorId, string action, string? entityId, string? pharmacyId,
        DateTime sinceUtc, CancellationToken ct)
    {
        var f = Builders<AuditEventDocument>.Filter;
        var filter = f.Eq(x => x.ActorId, actorId)
                     & f.Gte(x => x.AtUtc, sinceUtc)
                     & f.Eq(x => x.Action, action)
                     & f.Eq(x => x.EntityId, entityId)
                     & f.Eq(x => x.PharmacyId, pharmacyId);
        return await Collection(stream).Find(filter).Limit(1).AnyAsync(ct);
    }

    public async Task<AuditEventPage> SearchAsync(AuditStream stream, AuditEventQuery q, CancellationToken ct)
    {
        var col = Collection(stream);
        var filter = BuildFilter(q);

        // Counting is the expensive half on a log that only grows: capped, so
        // an unfiltered "all time" page costs a bounded scan. The console shows
        // the cap as "100,000+".
        var total = col.CountDocumentsAsync(filter, new CountOptions { Limit = CountCap }, ct);
        var items = col.Find(filter)
            // Time alone, no id tiebreaker: every audit index ends in atUtc, and
            // a second key would turn an index walk into an in-memory sort of
            // the whole match on a log that only grows. Oldest first walks the
            // same indexes backwards.
            .Sort(q.Sort == ListSort.Oldest
                ? Builders<AuditEventDocument>.Sort.Ascending(x => x.AtUtc)
                : Builders<AuditEventDocument>.Sort.Descending(x => x.AtUtc))
            .Skip(Math.Max(0, q.Skip))
            .Limit(Math.Clamp(q.Take, 1, MaxTake))
            .ToListAsync(ct);

        await Task.WhenAll(total, items);
        return new AuditEventPage(items.Result.Select(d => d.ToEvent()).ToList(), total.Result);
    }

    internal static FilterDefinition<AuditEventDocument> BuildFilter(AuditEventQuery q)
    {
        var f = Builders<AuditEventDocument>.Filter;
        var all = new List<FilterDefinition<AuditEventDocument>>();

        if (q.FromUtc is { } from) all.Add(f.Gte(x => x.AtUtc, from));
        if (q.ToUtc is { } to) all.Add(f.Lt(x => x.AtUtc, to));

        if (!string.IsNullOrWhiteSpace(q.Actor))
        {
            // An exact id (a user's, or "system:template-seeder"), or part of an
            // email address — the console has one box for both.
            var actor = q.Actor.Trim();
            all.Add(IsId(actor)
                ? f.Eq(x => x.ActorId, actor)
                : f.Or(
                    f.Eq(x => x.ActorId, actor),
                    f.Regex(x => x.ActorEmail, new BsonRegularExpression(Regex.Escape(actor), "i"))));
        }

        if (!string.IsNullOrWhiteSpace(q.PharmacyId)) all.Add(f.Eq(x => x.PharmacyId, q.PharmacyId.Trim()));

        if (!string.IsNullOrWhiteSpace(q.ActionPrefix))
            all.Add(f.Regex(x => x.Action, new BsonRegularExpression("^" + Regex.Escape(q.ActionPrefix.Trim()))));

        if (!string.IsNullOrWhiteSpace(q.EntityType)) all.Add(f.Eq(x => x.EntityType, q.EntityType.Trim()));
        if (!string.IsNullOrWhiteSpace(q.EntityId)) all.Add(f.Eq(x => x.EntityId, q.EntityId.Trim()));

        if (q.ActorKinds is { Count: > 0 } kinds) all.Add(f.In(x => x.ActorKind, kinds));

        if (q.SecurityDefault)
        {
            // What the security tab opens on: staff actions, sign-in events, and
            // anything refused — not a pharmacy editing its own posters, and not a
            // form that failed validation (400s are a support question, not security).
            all.Add(f.Or(
                f.Eq(x => x.ActorKind, ActorKinds.Admin),
                f.Regex(x => x.Action, new BsonRegularExpression(@"^auth\.")),
                f.Eq(x => x.Result, AuditResults.Denied)));
        }

        // The access rule, set from the caller's identity — never the request.
        if (q.VisiblePharmacyIds is not null || q.VisibleActorId is not null)
        {
            var visible = new List<FilterDefinition<AuditEventDocument>>();
            if (q.VisiblePharmacyIds is { Count: > 0 } ids) visible.Add(f.In(x => x.PharmacyId, ids));
            if (q.VisibleActorId is { } self) visible.Add(f.Eq(x => x.ActorId, self));
            // Nothing visible at all is a filter that matches nothing, not no filter.
            all.Add(visible.Count == 0 ? f.Eq(x => x.Id, "\u0000none") : f.Or(visible));
        }

        return all.Count == 0 ? f.Empty : f.And(all);
    }

    private static bool IsId(string s) =>
        s.Length == 32 && s.All(Uri.IsHexDigit);
}

/// <summary>The stored shape. Field names are the documented ones.</summary>
internal sealed class AuditEventDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("atUtc")] public DateTime AtUtc { get; set; }
    [BsonElement("actorId")] public string ActorId { get; set; } = default!;
    [BsonElement("actorEmail")] [BsonIgnoreIfNull] public string? ActorEmail { get; set; }
    [BsonElement("actorRole")] [BsonIgnoreIfNull] public string? ActorRole { get; set; }
    [BsonElement("actorKind")] public string ActorKind { get; set; } = default!;
    [BsonElement("pharmacyId")] public string? PharmacyId { get; set; }
    [BsonElement("action")] public string Action { get; set; } = default!;
    [BsonElement("entityType")] public string? EntityType { get; set; }
    [BsonElement("entityId")] public string? EntityId { get; set; }
    [BsonElement("result")] public string Result { get; set; } = default!;
    [BsonElement("ip")] [BsonIgnoreIfNull] public string? Ip { get; set; }
    [BsonElement("requestId")] [BsonIgnoreIfNull] public string? RequestId { get; set; }
    [BsonElement("meta")] public Dictionary<string, string> Meta { get; set; } = new();

    public static AuditEventDocument From(AuditEvent e) => new()
    {
        Id = e.Id,
        AtUtc = e.AtUtc,
        ActorId = e.ActorId,
        ActorEmail = e.ActorEmail,
        ActorRole = e.ActorRole,
        ActorKind = e.ActorKind,
        PharmacyId = e.PharmacyId,
        Action = e.Action,
        EntityType = e.EntityType,
        EntityId = e.EntityId,
        Result = e.Result,
        Ip = e.Ip,
        RequestId = e.RequestId,
        Meta = new Dictionary<string, string>(e.Meta, StringComparer.Ordinal),
    };

    public AuditEvent ToEvent() => new(
        Id, DateTime.SpecifyKind(AtUtc, DateTimeKind.Utc), ActorId, ActorEmail, ActorRole, ActorKind,
        PharmacyId, Action, EntityType, EntityId, Result, Ip, RequestId,
        Meta ?? new Dictionary<string, string>());
}
