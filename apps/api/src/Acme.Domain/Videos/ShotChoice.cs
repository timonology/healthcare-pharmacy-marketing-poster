using Acme.Domain.Common;

namespace Acme.Domain.Videos;

/// <summary>A shot the pharmacist tapped, and what it should show instead.</summary>
/// <remarks>
/// <para>
/// <b>The index is the tile they tapped, and nothing overrules it.</b> The
/// shot used to be inferred from their sentence by a model reading stock-library
/// titles, which refused "remove the last shot which shows medicine" because the
/// title said "employees on a couch". The pharmacist had watched the video.
/// </para>
/// <para>
/// <see cref="Show"/> may be empty: "change this shot" with nothing said about
/// what is searched for as the video's own subject by the worker.
/// </para>
/// </remarks>
public sealed record ShotChoice
{
    /// <summary>How many shots one request may change. Mirrors <c>MAX_EDITS</c> in shot_edits.py.</summary>
    public const int MaxPerRequest = 6;

    /// <summary>Long enough for a description, short enough to be a stock search.</summary>
    public const int MaxShowLength = 120;

    public int Index { get; }
    public string Show { get; }

    private ShotChoice(int index, string show)
    {
        Index = index;
        Show = show;
    }

    /// <summary>A choice read back from storage, already checked when it was made.</summary>
    /// <remarks>
    /// Storage does not keep the shot count it was checked against, and the
    /// worker re-validates every index against the video before using it.
    /// </remarks>
    public static ShotChoice Restore(int index, string? show) =>
        new(Math.Max(0, index), (show ?? string.Empty).Trim());

    /// <summary>A choice against a video whose shots are moments 0 … <paramref name="shotCount"/>−1.</summary>
    public static ShotChoice Create(int index, string? show, int shotCount) =>
        Create(index, show, Enumerable.Range(0, Math.Max(0, shotCount)).ToHashSet());

    /// <summary>A choice against the moments the video's tiles actually show.</summary>
    /// <remarks>
    /// <b>Membership, not a count.</b> A tile carries its moment number, and the
    /// moments on screen can have a gap — a moment whose clip would not open is
    /// skipped by the editor — so tiles 0, 1, 3 are three tiles whose last is
    /// moment 3. Checking <c>index &lt; count</c> refused exactly that tile.
    /// </remarks>
    public static ShotChoice Create(int index, string? show, IReadOnlySet<int> available)
    {
        if (!available.Contains(index))
            throw new DomainException(
                "That shot is no longer in this video. Close the change and open it again.");

        var text = (show ?? string.Empty).Trim();
        if (text.Length > MaxShowLength)
            throw new DomainException(
                $"Describe what the shot should show in {MaxShowLength} characters or fewer.");

        return new ShotChoice(index, text);
    }
}
