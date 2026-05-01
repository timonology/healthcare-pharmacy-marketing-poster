using Acme.Domain.Posters;

namespace Acme.Application.Posters;

public sealed record PosterQuery(
    string OwnerId,
    PosterStatus? Status = null,
    string? Search = null,
    int Skip = 0,
    int Take = 50);

public interface IPosterRepository
{
    Task<IReadOnlyList<Poster>> ListAsync(PosterQuery query, CancellationToken ct);
    Task<int> CountAsync(PosterQuery query, CancellationToken ct);
    Task<Poster?> FindByIdAsync(string id, CancellationToken ct);
    Task AddAsync(Poster poster, CancellationToken ct);
    Task UpdateAsync(Poster poster, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}
