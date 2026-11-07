namespace Acme.Application.Common;

/// <summary>Order for a list by its date field.</summary>
/// <remarks>
/// The same two choices on every searchable list (templates, posters, videos,
/// campaigns, patients, the admin users and pharmacies tables and the audit
/// log), so a pharmacist who learns them once knows them everywhere. Which date
/// each list orders by is the list's own business: see the repositories.
/// </remarks>
public enum ListSort
{
    Newest = 0,
    Oldest = 1,
}

/// <summary>
/// Sort and date range for one list request, as read from the query string.
/// </summary>
/// <remarks>
/// <para>
/// <b>From is inclusive, To is exclusive.</b> The browser turns the dates a
/// person picked into instants at the start of their local day, and sends
/// <c>to</c> as the start of the day <i>after</i> the last one they chose. So
/// "1–31 October 2026" in London is <c>from=2026-09-30T23:00Z&amp;to=2026-11-01T00:00Z</c>
/// (summer time on the 1st, winter time by the 31st),
/// and an item at 23:30 on the 31st local time is inside. Doing the day
/// arithmetic here would need the person's time zone, which only the browser
/// knows.
/// </para>
/// <para>
/// Parsed, not bound: an unknown sort word falls back to newest rather than
/// failing the request, because the value comes from a URL people share and
/// edit by hand.
/// </para>
/// </remarks>
public sealed record ListWindow(ListSort Sort = ListSort.Newest, DateTime? FromUtc = null, DateTime? ToUtc = null)
{
    public static readonly ListWindow Default = new();

    /// <summary>True when either end of the date range is set.</summary>
    public bool HasRange => FromUtc is not null || ToUtc is not null;

    public bool Ascending => Sort == ListSort.Oldest;

    /// <summary>Builds a window from raw query values.</summary>
    public static ListWindow From(string? sort, DateTime? from, DateTime? to) =>
        new(ParseSort(sort), Utc(from), Utc(to));

    /// <summary><c>oldest</c> (any case) is oldest; anything else, or nothing, is newest.</summary>
    public static ListSort ParseSort(string? value) =>
        string.Equals(value?.Trim(), "oldest", StringComparison.OrdinalIgnoreCase)
            ? ListSort.Oldest
            : ListSort.Newest;

    /// <summary>
    /// The instant as UTC. A value with no zone is taken to be UTC already —
    /// the browser always sends <c>Z</c>; a hand-typed date without one should
    /// not shift by the server's own offset.
    /// </summary>
    public static DateTime? Utc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } v => v,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        { } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
    };

    /// <summary>Whether an instant falls inside the range.</summary>
    /// <remarks>For in-memory checks and tests; the repositories filter in the database.</remarks>
    public bool Contains(DateTime atUtc) =>
        (FromUtc is not { } f || atUtc >= f) && (ToUtc is not { } t || atUtc < t);
}
