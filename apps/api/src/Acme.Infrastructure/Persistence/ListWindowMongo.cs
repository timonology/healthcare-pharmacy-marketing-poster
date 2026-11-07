using System.Linq.Expressions;
using Acme.Application.Common;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

/// <summary>
/// The date range and sort of a <see cref="ListWindow"/>, as Mongo filters and sorts.
/// </summary>
/// <remarks>
/// One place, so every list means the same thing by "from" (inclusive) and
/// "to" (exclusive), and so each one breaks ties by id in the same direction —
/// without a tiebreaker, items written in the same millisecond (an import, a
/// seeded batch) come back in an order Mongo does not promise, and a page
/// boundary can show one twice and skip another.
/// </remarks>
internal static class ListWindowMongo
{
    /// <summary>Range on a date field. Empty when the window has no range.</summary>
    public static FilterDefinition<T> Range<T>(Expression<Func<T, DateTime>> field, ListWindow w)
    {
        var b = Builders<T>.Filter;
        var filter = b.Empty;
        if (w.FromUtc is { } from) filter &= b.Gte(field, from);
        if (w.ToUtc is { } to) filter &= b.Lt(field, to);
        return filter;
    }

    /// <summary>The window's direction on a date field, then by id the same way.</summary>
    public static SortDefinition<T> Sort<T>(
        Expression<Func<T, object>> date, Expression<Func<T, object>> id, ListWindow w)
    {
        var s = Builders<T>.Sort;
        return w.Ascending
            ? s.Ascending(date).Ascending(id)
            : s.Descending(date).Descending(id);
    }
}
