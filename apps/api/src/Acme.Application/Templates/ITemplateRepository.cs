using Acme.Application.Common;
using Acme.Domain.Templates;

namespace Acme.Application.Templates;

public sealed record TemplateQuery(
    TemplateCategory? Category = null,
    string? Search = null,
    bool? IsPublished = true,
    int Skip = 0,
    int Take = 50,
    /// <summary>Sort and range on <c>CreatedAtUtc</c>. Null: newest first, all time.</summary>
    ListWindow? Window = null);

public interface ITemplateRepository
{
    Task<IReadOnlyList<Template>> ListAsync(TemplateQuery query, CancellationToken ct);
    Task<int> CountAsync(TemplateQuery query, CancellationToken ct);
    Task<Template?> FindByIdAsync(string id, CancellationToken ct);
    Task AddAsync(Template template, CancellationToken ct);
    Task UpdateAsync(Template template, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    Task<bool> AnyAsync(CancellationToken ct);
}
