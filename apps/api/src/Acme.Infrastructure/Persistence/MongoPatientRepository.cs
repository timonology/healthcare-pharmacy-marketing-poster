using Acme.Application.Common;
using Acme.Application.Patients;
using Acme.Domain.Patients;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoPatientRepository(MongoContext ctx) : IPatientRepository
{
    private IMongoCollection<PatientDocument> Col => ctx.Collection<PatientDocument>("patients");

    private static FilterDefinition<PatientDocument> BuildFilter(PatientQuery q)
    {
        var fb = Builders<PatientDocument>.Filter;
        var filter = fb.Eq(x => x.OwnerId, q.OwnerId);
        if (!string.IsNullOrWhiteSpace(q.GroupId))
            filter &= fb.AnyEq(x => x.GroupIds, q.GroupId);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var needle = q.Search.Trim();
            var regex = new MongoDB.Bson.BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(needle), "i");
            filter &= fb.Or(
                fb.Regex(x => x.FullName, regex),
                fb.Regex(x => x.Email, regex),
                fb.Regex(x => x.Phone, regex),
                fb.Regex(x => x.Notes, regex));
        }
        if (q.Window is { HasRange: true } w)
            filter &= ListWindowMongo.Range<PatientDocument>(x => x.CreatedAtUtc, w);
        return filter;
    }

    public async Task<IReadOnlyList<Patient>> ListAsync(PatientQuery query, CancellationToken ct)
    {
        // No window — imports, admin support views, recipient pickers — keeps
        // the alphabetical order those callers were built on. The Patients page
        // always sends one, and gets date added, newest first by default.
        var sort = query.Window is { } w
            ? ListWindowMongo.Sort<PatientDocument>(x => x.CreatedAtUtc, x => x.Id, w)
            : Builders<PatientDocument>.Sort.Ascending(x => x.FullName);
        var docs = await Col.Find(BuildFilter(query))
            .Sort(sort)
            .Skip(query.Skip)
            .Limit(query.Take)
            .ToListAsync(ct);
        return docs.Select(PatientMapper.Hydrate).ToList();
    }

    public async Task<int> CountAsync(PatientQuery query, CancellationToken ct) =>
        (int)await Col.CountDocumentsAsync(BuildFilter(query), cancellationToken: ct);

    public async Task<Patient?> FindByIdAsync(string id, CancellationToken ct)
    {
        var doc = await Col.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc is null ? null : PatientMapper.Hydrate(doc);
    }

    public async Task<IReadOnlyList<Patient>> FindManyByIdsAsync(
        string ownerId,
        IReadOnlyList<string> ids,
        CancellationToken ct)
    {
        if (ids.Count == 0) return Array.Empty<Patient>();
        var fb = Builders<PatientDocument>.Filter;
        var filter = fb.Eq(x => x.OwnerId, ownerId) & fb.In(x => x.Id, ids);
        var docs = await Col.Find(filter).ToListAsync(ct);
        return docs.Select(PatientMapper.Hydrate).ToList();
    }

    public async Task<IReadOnlyList<Patient>> ListByGroupAsync(
        string ownerId,
        string groupId,
        CancellationToken ct)
    {
        var fb = Builders<PatientDocument>.Filter;
        var filter = fb.Eq(x => x.OwnerId, ownerId) & fb.AnyEq(x => x.GroupIds, groupId);
        var docs = await Col.Find(filter).ToListAsync(ct);
        return docs.Select(PatientMapper.Hydrate).ToList();
    }

    /// <summary>
    /// Member counts per group for one pharmacy, in a single aggregation.
    /// </summary>
    /// <remarks>
    /// Untyped pipeline for the same reason the analytics ones are: $unwind on
    /// an array field has no clean typed equivalent, and pushing it through the
    /// driver's expression translator is the kind of code that compiles and
    /// then throws on a version bump.
    ///
    /// The $match comes first so the unwind only ever touches one pharmacy's
    /// documents — reversing them would fan out the whole collection before
    /// filtering.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, int>> CountsByGroupAsync(
        string ownerId, CancellationToken ct)
    {
        var collection = ctx.Collection<MongoDB.Bson.BsonDocument>("patients");

        var pipeline = PipelineDefinition<MongoDB.Bson.BsonDocument, MongoDB.Bson.BsonDocument>.Create(
            new MongoDB.Bson.BsonDocument("$match", new MongoDB.Bson.BsonDocument
            {
                { "ownerId", ownerId },
                { "groupIds", new MongoDB.Bson.BsonDocument("$exists", true) },
            }),
            new MongoDB.Bson.BsonDocument("$unwind", "$groupIds"),
            new MongoDB.Bson.BsonDocument("$group", new MongoDB.Bson.BsonDocument
            {
                { "_id", "$groupIds" },
                { "count", new MongoDB.Bson.BsonDocument("$sum", 1) },
            }));

        var rows = await collection
            .Aggregate<MongoDB.Bson.BsonDocument>(pipeline, cancellationToken: ct)
            .ToListAsync(ct);

        var result = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            var id = row["_id"];
            if (id.IsBsonNull) continue;
            result[id.AsString] = row["count"].ToInt32();
        }

        return result;
    }

    /// <summary>
    /// Pulls a group id out of every patient that references it, platform-wide.
    /// </summary>
    /// <remarks>
    /// One $pull across the collection rather than loading and rewriting each
    /// patient. A global group may be referenced by hundreds of pharmacies'
    /// patients, and the read-modify-write loop the private path uses would be
    /// both slow and lossy if it failed halfway.
    /// </remarks>
    public async Task<long> RemoveGroupFromAllPatientsAsync(string groupId, CancellationToken ct)
    {
        var result = await Col.UpdateManyAsync(
            Builders<PatientDocument>.Filter.AnyEq(x => x.GroupIds, groupId),
            Builders<PatientDocument>.Update.Pull(x => x.GroupIds, groupId),
            cancellationToken: ct);

        return result.ModifiedCount;
    }

    public Task<long> CountInGroupAcrossPlatformAsync(string groupId, CancellationToken ct) =>
        Col.CountDocumentsAsync(
            Builders<PatientDocument>.Filter.AnyEq(x => x.GroupIds, groupId),
            cancellationToken: ct);

    public Task AddAsync(Patient patient, CancellationToken ct) =>
        Col.InsertOneAsync(PatientMapper.ToDocument(patient), cancellationToken: ct);

    public Task AddManyAsync(IEnumerable<Patient> patients, CancellationToken ct)
    {
        var docs = patients.Select(PatientMapper.ToDocument).ToList();
        if (docs.Count == 0) return Task.CompletedTask;
        return Col.InsertManyAsync(docs, cancellationToken: ct);
    }

    public Task UpdateAsync(Patient patient, CancellationToken ct) =>
        Col.ReplaceOneAsync(x => x.Id == patient.Id, PatientMapper.ToDocument(patient), cancellationToken: ct);

    public Task DeleteAsync(string id, CancellationToken ct) =>
        Col.DeleteOneAsync(x => x.Id == id, ct);

    public Task DeleteManyAsync(string ownerId, IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return Task.CompletedTask;
        var fb = Builders<PatientDocument>.Filter;
        var filter = fb.Eq(x => x.OwnerId, ownerId) & fb.In(x => x.Id, ids);
        return Col.DeleteManyAsync(filter, ct);
    }

    public async Task<int> CountByOwnerAsync(string ownerId, CancellationToken ct) =>
        (int)await Col.CountDocumentsAsync(x => x.OwnerId == ownerId, cancellationToken: ct);
}
