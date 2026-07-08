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
        return filter;
    }

    public async Task<IReadOnlyList<Patient>> ListAsync(PatientQuery query, CancellationToken ct)
    {
        var docs = await Col.Find(BuildFilter(query))
            .SortBy(x => x.FullName)
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
