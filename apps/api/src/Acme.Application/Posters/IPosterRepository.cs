using Acme.Application.Common;
using Acme.Domain.Posters;

namespace Acme.Application.Posters;

public sealed record PosterQuery(
    string OwnerId,
    PosterStatus? Status = null,
    string? Search = null,
    int Skip = 0,
    int Take = 50,
    /// <summary>Sort and range on <c>UpdatedAtUtc</c>. Null: newest first, all time.</summary>
    ListWindow? Window = null);

public interface IPosterRepository
{
    Task<IReadOnlyList<Poster>> ListAsync(PosterQuery query, CancellationToken ct);
    Task<int> CountAsync(PosterQuery query, CancellationToken ct);
    Task<Poster?> FindByIdAsync(string id, CancellationToken ct);

    /// <summary>Several posters at once, scoped to one owner.</summary>
    /// <remarks>
    /// Added for the display kiosk, which resolves a whole playlist on every
    /// poll. One screen with a fifty-item loop was fifty queries every thirty
    /// seconds, forever, multiplied by every screen in the estate — and the
    /// ETag does not help, because the content has to be built before its
    /// revision can be computed.
    /// </remarks>
    Task<IReadOnlyList<Poster>> FindManyByIdsAsync(
        string ownerId, IReadOnlyList<string> ids, CancellationToken ct);
    Task AddAsync(Poster poster, CancellationToken ct);
    Task UpdateAsync(Poster poster, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);

    /// <summary>Platform-wide poster counts for the admin dashboard.</summary>
    Task<(long Total, long ThisMonth)> CountAllAsync(CancellationToken ct);
}
