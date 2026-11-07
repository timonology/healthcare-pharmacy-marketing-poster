using Acme.Application.Common;
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

    /// <summary>The built-in house set, which the gallery shows first.</summary>
    public const string HousePrefix = "sys-house-";

    /// <summary>
    /// A page of templates: the house set first, then everything else, each
    /// newest design first — or oldest first, and within a created-date range,
    /// when the query's window says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// House first because it is the current design language — the one the AI
    /// designer and the marketing site use — while older system templates and
    /// admin-published designs stay available after it. Ordering by date
    /// alone would put any template published later (an admin's AI design,
    /// say) ahead of the whole set.
    /// </para>
    /// <para>
    /// Two plain queries rather than an aggregation with a computed rank:
    /// a prefix match on <c>_id</c> uses the primary index, and nothing here
    /// depends on aggregation operators the hosted Mongo API may not have.
    /// The page is stitched across the boundary, so paging stays exact.
    /// </para>
    /// <para>
    /// Within each group: CreatedAtUtc, not UpdatedAtUtc, so fixing a typo in
    /// an old template doesn't jump it ahead of genuinely new work; Id as the
    /// tiebreaker, because seeded batches share a date and Mongo's order
    /// within a batch is otherwise unspecified.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Template>> ListAsync(TemplateQuery query, CancellationToken ct)
    {
        var b = Builders<TemplateDocument>.Filter;
        var filter = BuildFilter(query);
        var isHouse = b.Regex(x => x.Id, new MongoDB.Bson.BsonRegularExpression("^" + HousePrefix));
        var house = filter & isHouse;
        var others = filter & b.Not(isHouse);

        var window = query.Window ?? ListWindow.Default;
        var skip = Math.Max(0, query.Skip);
        var take = Math.Max(0, query.Take);
        if (take == 0) return [];

        var houseCount = (int)await _collection.CountDocumentsAsync(house, cancellationToken: ct);
        var docs = new List<TemplateDocument>(Math.Min(take, 100));

        if (skip < houseCount)
            docs.AddRange(await Page(house, skip, take, window, ct));

        var remaining = take - docs.Count;
        if (remaining > 0)
            docs.AddRange(await Page(others, Math.Max(0, skip - houseCount), remaining, window, ct));

        return docs.Select(TemplateMapper.Hydrate).ToList();
    }

    private Task<List<TemplateDocument>> Page(
        FilterDefinition<TemplateDocument> filter, int skip, int take, ListWindow window, CancellationToken ct) =>
        _collection
            .Find(filter)
            // Newest first keeps the original tiebreaker (id ascending), so the
            // default order is exactly what it was before sorting was offered.
            .Sort(window.Ascending
                ? Builders<TemplateDocument>.Sort.Ascending(x => x.CreatedAtUtc).Ascending(x => x.Id)
                : Builders<TemplateDocument>.Sort.Descending(x => x.CreatedAtUtc).Ascending(x => x.Id))
            .Skip(skip)
            .Limit(take)
            .ToListAsync(ct);

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

        if (q.Window is { HasRange: true } w)
            filter &= ListWindowMongo.Range<TemplateDocument>(x => x.CreatedAtUtc, w);

        return filter;
    }
}
