using Acme.Domain.Common;

namespace Acme.Domain.Displays;

/// <summary>What a playlist slot points at.</summary>
/// <remarks>
/// A video slot plays the video once, for its own length. Only a Ready video
/// the pharmacy owns can be added; one that later fails, is deleted or is
/// being re-cut is kept in the playlist and skipped by the screen.
/// </remarks>
public enum PlaylistItemKind
{
    Poster = 1,
    Video = 2,
}

/// <summary>One slot in a playlist: what to show, and for how long.</summary>
/// <param name="Kind">Poster or video.</param>
/// <param name="RefId">The poster or video id.</param>
/// <param name="DurationSeconds">
/// How long the slot holds the screen. Per-item rather than per-playlist
/// because a dense leaflet and a single-line opening-hours notice do not
/// deserve the same dwell time.
/// </param>
public readonly record struct PlaylistItem(
    PlaylistItemKind Kind,
    string RefId,
    int DurationSeconds);

/// <summary>
/// An ordered set of posters, shown on one or more screens.
/// </summary>
/// <remarks>
/// <para>
/// A first-class entity rather than a list hanging off a screen, because the
/// second screen is where the value is: a pharmacy with a counter display and a
/// waiting-room display wants one edit to reach both, and a head office running
/// a flu campaign wants one edit to reach every branch it works as.
/// </para>
/// <para>
/// <b>Changing a playlist is what makes screens update.</b> <see
/// cref="Version"/> increments on every content change and is what the kiosk's
/// ETag is built from, so a screen showing this playlist notices within one
/// poll and nothing has to be pushed to it.
/// </para>
/// </remarks>
public sealed class Playlist : Entity
{
    /// <summary>Shortest slot that is not a subliminal flash.</summary>
    public const int MinDurationSeconds = 3;

    /// <summary>Longest slot before a screen looks frozen to a passer-by.</summary>
    public const int MaxDurationSeconds = 300;

    public const int DefaultDurationSeconds = 12;

    /// <summary>Beyond this a playlist is unreviewable, and the kiosk cache is large.</summary>
    public const int MaxItems = 50;

    /// <summary>
    /// Videos per playlist. Screens stream video rather than caching it, so
    /// every one is a download on every loop; ten keeps a shop's bandwidth and
    /// the loop's length sensible.
    /// </summary>
    public const int MaxVideos = 10;

    public string OwnerId { get; private set; } = default!;
    public string Name { get; private set; } = default!;

    private readonly List<PlaylistItem> _items = new();
    public IReadOnlyList<PlaylistItem> Items => _items;

    /// <summary>
    /// Bumped whenever the content changes. The kiosk's ETag is derived from
    /// this, so it must never move for a rename that a viewer cannot see —
    /// otherwise every screen re-downloads its images because somebody fixed a
    /// typo in a playlist name.
    /// </summary>
    public int Version { get; private set; } = 1;

    private Playlist() { }

    public static Playlist Create(string ownerId, string name)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new DomainException("OwnerId is required.");

        return new Playlist
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            Name = CleanName(name),
        };
    }

    public void Rename(string name)
    {
        var cleaned = CleanName(name);
        if (cleaned == Name) return;

        // No version bump: a rename changes nothing a screen displays.
        Name = cleaned;
        Touch();
    }

    /// <summary>Replaces the whole running order.</summary>
    /// <remarks>
    /// Wholesale rather than add/remove/move, because the editor works on the
    /// whole list and sends it back. Three fine-grained operations would each
    /// need their own concurrency story for no gain.
    /// </remarks>
    public void SetItems(IReadOnlyList<PlaylistItem> items)
    {
        if (items.Count > MaxItems)
            throw new DomainException($"A playlist holds at most {MaxItems} items.");

        if (items.Count(i => i.Kind == PlaylistItemKind.Video) > MaxVideos)
            throw new DomainException($"A playlist holds at most {MaxVideos} videos.");

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.RefId))
                throw new DomainException("Every playlist item needs something to show.");

            if (item.DurationSeconds is < MinDurationSeconds or > MaxDurationSeconds)
                throw new DomainException(
                    $"Each item must stay on screen between {MinDurationSeconds} and "
                    + $"{MaxDurationSeconds} seconds.");
        }

        // The same poster twice in a row is almost always a mistake, but the
        // same poster at the start and end of a long loop is not — so
        // duplicates are allowed and only adjacency is worth refusing.
        for (var i = 1; i < items.Count; i++)
        {
            if (items[i].Kind == items[i - 1].Kind && items[i].RefId == items[i - 1].RefId)
                throw new DomainException(
                    "The same item twice in a row would look like the screen had frozen.");
        }

        if (_items.SequenceEqual(items)) return;

        _items.Clear();
        _items.AddRange(items);
        Version++;
        Touch();
    }

    private static string CleanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("A playlist needs a name.");

        var cleaned = name.Trim();
        if (cleaned.Length > 120)
            throw new DomainException("Playlist name must be 120 characters or fewer.");

        return cleaned;
    }
}
