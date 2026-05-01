using Acme.Application.Templates;
using Acme.Domain.Templates;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoTemplateRepository : ITemplateRepository
{
    private const string CollectionName = "templates";
    private readonly IMongoCollection<TemplateDocument> _collection;

    public MongoTemplateRepository(MongoContext ctx)
    {
        // Indexes are declared centrally in MongoIndexInitializer.
        _collection = ctx.Collection<TemplateDocument>(CollectionName);
    }

    public async Task<IReadOnlyList<Template>> ListAsync(TemplateQuery query, CancellationToken ct)
    {
        var filter = BuildFilter(query);
        var docs = await _collection
            .Find(filter)
            .SortByDescending(x => x.UpdatedAtUtc)
            .Skip(query.Skip)
            .Limit(query.Take)
            .ToListAsync(ct);
        return docs.Select(TemplateMapper.Hydrate).ToList();
    }

    public async Task<int> CountAsync(TemplateQuery query, CancellationToken ct) =>
        (int)await _collection.CountDocumentsAsync(BuildFilter(query), cancellationToken: ct);

    public async Task<Template?> FindByIdAsync(string id, CancellationToken ct)
    {
        var doc = await _collection.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc is null ? null : TemplateMapper.Hydrate(doc);
    }

    public Task AddAsync(Template template, CancellationToken ct) =>
        _collection.InsertOneAsync(TemplateMapper.ToDocument(template), cancellationToken: ct);

    public Task UpdateAsync(Template template, CancellationToken ct) =>
        _collection.ReplaceOneAsync(
            x => x.Id == template.Id,
            TemplateMapper.ToDocument(template),
            cancellationToken: ct);

    public Task DeleteAsync(string id, CancellationToken ct) =>
        _collection.DeleteOneAsync(x => x.Id == id, ct);

    public async Task<bool> AnyAsync(CancellationToken ct) =>
        await _collection.CountDocumentsAsync(FilterDefinition<TemplateDocument>.Empty, cancellationToken: ct) > 0;

    private static FilterDefinition<TemplateDocument> BuildFilter(TemplateQuery q)
    {
        var b = Builders<TemplateDocument>.Filter;
        var filter = b.Empty;

        if (q.Category is { } cat)
            filter &= b.Eq(x => x.Category, (int)cat);

        if (q.IsPublished is { } pub)
            filter &= b.Eq(x => x.IsPublished, pub);

        if (!string.IsNullOrWhiteSpace(q.Search))
            filter &= b.Text(q.Search);

        return filter;
    }
}
