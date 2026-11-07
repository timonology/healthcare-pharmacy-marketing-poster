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

    public async Task<IReadOnlyList<PatientGroup>> ListGlobalAsync(CancellationToken ct)
    {
        var docs = await Col.Find(x => x.Scope == (int)GroupScope.Global)
            .SortBy(x => x.Name)
            .ToListAsync(ct);
        return docs.Select(PatientMapper.HydrateGroup).ToList();
    }

    /// <summary>
    /// A pharmacy's own groups plus every global one, in a single query.
    /// </summary>
    /// <remarks>
    /// One round trip rather than two and a merge in memory. The ordering puts
    /// private groups first — a pharmacy's own list is what they came for, and
    /// the shared ones are context.
    /// </remarks>
    public async Task<IReadOnlyList<PatientGroup>> ListVisibleToAsync(
        string pharmacyId, CancellationToken ct)
    {
        var fb = Builders<PatientGroupDocument>.Filter;

        // The scope test is "not global" rather than "== private" so a group
        // written before the field existed — no scope at all — still belongs to
        // its owner rather than disappearing from their list.
        var mine = fb.Eq(x => x.OwnerId, pharmacyId) & fb.Ne(x => x.Scope, (int)GroupScope.Global);
        var global = fb.Eq(x => x.Scope, (int)GroupScope.Global);

        var docs = await Col.Find(fb.Or(mine, global))
            .SortBy(x => x.Scope).ThenBy(x => x.Name)
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
