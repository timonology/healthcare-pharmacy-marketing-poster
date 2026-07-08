using Acme.Application.Patients;
using Acme.Domain.Patients;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoPatientGroupRepository(MongoContext ctx) : IPatientGroupRepository
{
    private IMongoCollection<PatientGroupDocument> Col =>
        ctx.Collection<PatientGroupDocument>("patient_groups");

    public async Task<IReadOnlyList<PatientGroup>> ListByOwnerAsync(string ownerId, CancellationToken ct)
    {
        var docs = await Col.Find(x => x.OwnerId == ownerId)
            .SortBy(x => x.Name)
            .ToListAsync(ct);
        return docs.Select(PatientMapper.HydrateGroup).ToList();
    }

    public async Task<PatientGroup?> FindByIdAsync(string id, CancellationToken ct)
    {
        var doc = await Col.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc is null ? null : PatientMapper.HydrateGroup(doc);
    }

    public Task AddAsync(PatientGroup group, CancellationToken ct) =>
        Col.InsertOneAsync(PatientMapper.ToDocument(group), cancellationToken: ct);

    public Task UpdateAsync(PatientGroup group, CancellationToken ct) =>
        Col.ReplaceOneAsync(x => x.Id == group.Id, PatientMapper.ToDocument(group), cancellationToken: ct);

    public Task DeleteAsync(string id, CancellationToken ct) =>
        Col.DeleteOneAsync(x => x.Id == id, ct);
}
