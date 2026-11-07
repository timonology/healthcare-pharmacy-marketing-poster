using System.Text.Json;

namespace Acme.Application.Videos;

/// <summary>When one moment was on screen, as the worker rendered it.</summary>
/// <param name="Segment">The moment; null for a filler shot that belongs to none.</param>
/// <param name="ThumbKey">Where the worker kept a still of it, if it did.</param>
public sealed record ShotTiming(int? Segment, double Start, double End, string? ThumbKey);

/// <summary>What footage one moment came from, as the worker recorded it.</summary>
/// <param name="Role">"aligned" (stock), "user" (the pharmacy's photo) or "extra".</param>
/// <param name="Shows">The stock library's description of the clip, if known.</param>
public sealed record ShotClip(int? Segment, string? Role, string? Shows);

/// <summary>The worker-written fields a shot list is built from.</summary>
/// <remarks>
/// Read straight from storage rather than through the <c>Video</c> aggregate:
/// these fields are the worker's, the domain never changes them, and loading
/// them into the aggregate would invite a later write to send them back.
/// </remarks>
public sealed record VideoShotSource(
    string OwnerId,
    string? MetadataJson,
    IReadOnlyList<ShotTiming> Timeline,
    IReadOnlyList<ShotClip> Clips);

/// <summary>One tile in the refine dialog's shot picker.</summary>
/// <param name="Index">The moment. What a tapped tile sends back.</param>
/// <param name="Caption">What is said over it — the words the pharmacist hears.</param>
/// <param name="IsPhoto">The pharmacy's own photograph, not stock.</param>
/// <param name="HasThumbnail">A still exists; otherwise the tile is numbered.</param>
/// <param name="Shows">The stock description, as alt text only. Never used to choose.</param>
public sealed record VideoShotDto(
    int Index,
    double StartSeconds,
    double EndSeconds,
    string Caption,
    bool IsPhoto,
    bool HasThumbnail,
    string? Shows);

/// <summary>A tapped tile and what it should show.</summary>
public sealed record ShotChoiceDto(int Index, string? Show = null);

/// <summary>The shots the pharmacist tapped in the picker.</summary>
public sealed record ChangeShotsRequest(IReadOnlyList<ShotChoiceDto> Shots);

/// <summary>Builds the shot list the pharmacist picks from.</summary>
public static class VideoShots
{
    /// <summary>
    /// The render's own clip cap, mirroring <c>MAX_CLIPS_PER_VIDEO</c>'s default
    /// in nodes.py. Only used for videos rendered before the timeline was
    /// stored; newer ones list exactly the shots the timeline records.
    /// </summary>
    public const int MaxShots = 12;

    /// <summary>
    /// One tile per moment, in order. Times come from the rendered timeline when
    /// the worker stored one, and from the script's plan for older videos.
    /// </summary>
    /// <remarks>
    /// Tolerant of anything the worker, or an older worker, wrote: unreadable
    /// metadata gives an empty list, which the dialog shows as "this video's
    /// shots are not on record" rather than an error.
    /// </remarks>
    public static IReadOnlyList<VideoShotDto> Build(VideoShotSource source)
    {
        var segments = ReadSegments(source.MetadataJson);
        if (segments.Count == 0) return Array.Empty<VideoShotDto>();

        var timed = source.Timeline
            .Where(t => t.Segment is { } s && s >= 0 && s < segments.Count)
            .GroupBy(t => t.Segment!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var photos = source.Clips
            .Where(c => c.Segment is not null && c.Role == "user")
            .Select(c => c.Segment!.Value)
            .ToHashSet();
        var shows = source.Clips
            .Where(c => c.Segment is not null && c.Role == "aligned" && !string.IsNullOrWhiteSpace(c.Shows))
            .GroupBy(c => c.Segment!.Value)
            .ToDictionary(g => g.Key, g => g.First().Shows);

        // A rendered timeline lists the moments that were actually on screen;
        // without one, the plan's moments up to the render's cap.
        var indexes = timed.Count > 0
            ? timed.Keys.OrderBy(i => i).ToList()
            : Enumerable.Range(0, Math.Min(segments.Count, MaxShots)).ToList();

        return indexes.Select(i =>
        {
            var (plannedStart, plannedEnd, text) = segments[i];
            var t = timed.GetValueOrDefault(i);
            return new VideoShotDto(
                Index: i,
                StartSeconds: Math.Round(t?.Start ?? plannedStart, 2),
                EndSeconds: Math.Round(t?.End ?? plannedEnd, 2),
                Caption: text,
                IsPhoto: photos.Contains(i),
                HasThumbnail: !string.IsNullOrWhiteSpace(t?.ThumbKey),
                Shows: shows.GetValueOrDefault(i));
        }).ToList();
    }

    /// <summary>
    /// The storage key of a moment's still, or null — refusing any key that is
    /// not under this video's own <c>shots/</c> folder.
    /// </summary>
    /// <remarks>
    /// The key is written by the worker and read back from the document; it is
    /// checked anyway, because this endpoint streams whatever key it is given
    /// and a hand-edited row should not be able to point it at another
    /// pharmacy's files.
    /// </remarks>
    public static string? ThumbnailKey(VideoShotSource source, string videoId, int index)
    {
        var key = source.Timeline.FirstOrDefault(t => t.Segment == index)?.ThumbKey;
        var folder = $"videos/{source.OwnerId}/{videoId}/shots/";
        return key is not null && key.StartsWith(folder, StringComparison.Ordinal)
            && !key.Contains("..", StringComparison.Ordinal)
            ? key
            : null;
    }

    private static List<(double Start, double End, string Text)> ReadSegments(string? json)
    {
        var result = new List<(double, double, string)>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("segments", out var segments)
                || segments.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var seg in segments.EnumerateArray())
            {
                if (seg.ValueKind != JsonValueKind.Object) continue;
                result.Add((Number(seg, "start"), Number(seg, "end"), Text(seg)));
            }
        }
        catch (JsonException)
        {
            // Unreadable metadata: no shots on record, said so by the dialog.
        }
        return result;
    }

    private static double Number(JsonElement seg, string name) =>
        seg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : 0d;

    private static string Text(JsonElement seg)
    {
        var text = seg.TryGetProperty("text", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 90 ? text : text[..89].TrimEnd() + "…";
    }
}
