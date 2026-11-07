using Acme.Application.Common;
using Acme.Domain.Videos;

namespace Acme.Application.Videos;

public interface IVideoRepository
{
    Task<IReadOnlyList<Video>> ListByOwnerAsync(string ownerId, CancellationToken ct);

    /// <summary>The Videos page: one owner's videos, sorted and ranged on <c>CreatedAtUtc</c>.</summary>
    /// <remarks>
    /// The default body exists so test doubles keep compiling; the Mongo
    /// repository overrides it with a database query.
    /// </remarks>
    async Task<IReadOnlyList<Video>> ListAsync(string ownerId, ListWindow window, CancellationToken ct)
    {
        var all = await ListByOwnerAsync(ownerId, ct);
        var inRange = all.Where(v => window.Contains(v.CreatedAtUtc));
        return (window.Ascending
                ? inRange.OrderBy(v => v.CreatedAtUtc).ThenBy(v => v.Id, StringComparer.Ordinal)
                : inRange.OrderByDescending(v => v.CreatedAtUtc).ThenByDescending(v => v.Id, StringComparer.Ordinal))
            .ToList();
    }
    Task<Video?> FindByIdAsync(string id, CancellationToken ct);

    /// <summary>
    /// The given videos, scoped to one owner in the query itself — a playlist
    /// that somehow named another pharmacy's video resolves to nothing.
    /// </summary>
    Task<IReadOnlyList<Video>> FindManyByIdsAsync(
        string ownerId, IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>The worker-written fields a shot list is built from. Null if no such video.</summary>
    Task<VideoShotSource?> FindShotSourceAsync(string id, CancellationToken ct);
    Task<int> CountByOwnerThisMonthAsync(string ownerId, CancellationToken ct);

    /// <summary>Platform-wide counts for the admin dashboard.</summary>
    Task<long> CountAllAsync(CancellationToken ct);
    Task<long> CountAllThisMonthAsync(CancellationToken ct);
    Task AddAsync(Video video, CancellationToken ct);
    Task UpdateAsync(Video video, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}
