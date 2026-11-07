using System.Globalization;
using Acme.Domain.Social;

namespace Acme.Application.Social;

/// <summary>How often, as the browser sends it.</summary>
/// <param name="Time">Local wall-clock time, <c>HH:mm</c>.</param>
/// <param name="Dates">For specific dates: local calendar days, <c>yyyy-MM-dd</c>.</param>
public sealed record CadenceRequest(
    CadenceKind Kind,
    string Time,
    IReadOnlyList<DayOfWeek>? Days = null,
    int? IntervalDays = null,
    IReadOnlyList<string>? Dates = null);

/// <summary>Create or edit a series. On edit, the source is fixed and ignored.</summary>
/// <param name="StartDate">Local calendar day, <c>yyyy-MM-dd</c>.</param>
/// <param name="EndDate">Last local day that may have a post, inclusive. Null: until they stop it.</param>
/// <param name="PostNow">Also post once straight away (create only).</param>
public sealed record SeriesRequest(
    string? Name,
    PublishSourceKind SourceKind,
    string SourceId,
    string? Caption,
    IReadOnlyList<string> ConnectionIds,
    CadenceRequest Cadence,
    string? TimeZoneId,
    string StartDate,
    string? EndDate = null,
    SeriesNotify? Notify = null,
    bool PostNow = false);

/// <summary>The When step's preview: the dates this rule would post on.</summary>
public sealed record SeriesPreviewRequest(
    CadenceRequest Cadence,
    string? TimeZoneId,
    string StartDate,
    string? EndDate = null,
    int? Count = null);

/// <summary>One occurrence, already in the pharmacy's words and clock.</summary>
/// <param name="Label">"Tue 28 Oct, 09:00 UK"</param>
/// <param name="LocalDate"><c>yyyy-MM-dd</c> on the pharmacy's clock, for the calendar strip.</param>
/// <param name="LocalTime"><c>HH:mm</c> on the pharmacy's clock.</param>
public sealed record SeriesTimeDto(DateTime AtUtc, string Label, string LocalDate, string LocalTime);

public sealed record SeriesPreviewDto(
    IReadOnlyList<SeriesTimeDto> Next,
    string CadenceText,
    string TimeZoneLabel,
    /// <summary>Why the rule cannot be used, or null. The preview still answers rather than erroring.</summary>
    string? Problem);

/// <param name="Status">Null when the account has been disconnected and removed.</param>
public sealed record SeriesDestinationDto(
    string ConnectionId,
    SocialPlatform Platform,
    string PlatformName,
    string DisplayName,
    ConnectionStatus? Status,
    bool NeedsReconnect);

public sealed record SeriesOccurrenceDto(
    string PostId,
    DateTime OccurrenceAtUtc,
    string Label,
    PublishStatus Status,
    IReadOnlyList<PublishTargetDto> Targets);

public sealed record RecurringSeriesDto(
    string Id,
    string Name,
    PublishSourceKind SourceKind,
    string SourceId,
    string SourceName,
    string Caption,
    IReadOnlyList<SeriesDestinationDto> Destinations,
    CadenceKind CadenceKind,
    string Time,
    IReadOnlyList<DayOfWeek> Days,
    int IntervalDays,
    IReadOnlyList<string> Dates,
    string TimeZoneId,
    string TimeZoneLabel,
    string StartDate,
    string? EndDate,
    SeriesStatus Status,
    string? StatusReason,
    bool PausedBySystem,
    SeriesNotify Notify,
    /// <summary>"Every Mon and Thu at 9:00"</summary>
    string CadenceText,
    DateTime? NextOccurrenceUtc,
    /// <summary>"Tue 28 Oct, 09:00 UK", or null when nothing is coming.</summary>
    string? NextLabel,
    /// <summary>Occurrences in the next 14 days, for the calendar strip. Empty unless active.</summary>
    IReadOnlyList<SeriesTimeDto> Upcoming,
    IReadOnlyList<SeriesOccurrenceDto> Recent,
    int MissedCount,
    DateTime CreatedAtUtc);

public sealed record RecurringSeriesListDto(
    IReadOnlyList<RecurringSeriesDto> Items,
    int ActiveCount,
    /// <summary>Active series the plan allows. 0: not on this plan.</summary>
    int MaxActive,
    string? PlanReason);

/// <summary>Series in words and on the pharmacy's clock.</summary>
public static class SeriesWording
{
    // Spelled out rather than taken from an en-GB culture: the wording must not
    // depend on which ICU data a container happens to ship.
    private static readonly string[] Months =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private static string DayMonth(DateOnly d) => $"{d.Day} {Months[d.Month - 1]}";

    /// <summary>"UK" for London, otherwise the place name from the zone id.</summary>
    public static string ZoneLabel(string timeZoneId) => timeZoneId switch
    {
        "Europe/London" or "GB" or "GMT Standard Time" => "UK",
        _ => timeZoneId.Contains('/')
            ? timeZoneId[(timeZoneId.LastIndexOf('/') + 1)..].Replace('_', ' ') + " time"
            : timeZoneId,
    };

    /// <summary>"9:00" — no leading zero in prose; the labels keep "09:00".</summary>
    public static string Clock(TimeOnly t) => t.ToString("H:mm", CultureInfo.InvariantCulture);

    /// <summary>"Every day at 9:00", "Every Mon and Thu at 9:00", "Every 3 days at 9:00", "On 12 Nov and 3 Dec at 9:00".</summary>
    public static string Describe(Cadence c)
    {
        var at = $"at {Clock(c.TimeOfDay)}";
        switch (c.Kind)
        {
            case CadenceKind.Daily:
                return $"Every day {at}";

            case CadenceKind.Weekly:
                var days = (c.Days ?? Array.Empty<DayOfWeek>()).OrderBy(d => ((int)d + 6) % 7).ToList();
                if (days.Count == 7) return $"Every day {at}";
                if (days.Count == 5 && !days.Contains(DayOfWeek.Saturday) && !days.Contains(DayOfWeek.Sunday))
                    return $"Every weekday {at}";
                return $"Every {JoinAnd(days.Select(Short))} {at}";

            case CadenceKind.EveryNDays:
                return c.IntervalDays <= 1 ? $"Every day {at}" : $"Every {c.IntervalDays} days {at}";

            case CadenceKind.SpecificDates:
                var dates = (c.Dates ?? Array.Empty<DateOnly>()).Order().ToList();
                if (dates.Count == 0) return $"On chosen dates {at}";
                if (dates.Count > 3) return $"On {dates.Count} chosen dates {at}";
                return $"On {JoinAnd(dates.Select(DayMonth))} {at}";

            default:
                return "On a schedule";
        }
    }

    /// <summary>"Tue 28 Oct, 09:00 UK"</summary>
    public static SeriesTimeDto Time(DateTime utc, TimeZoneInfo zone, string timeZoneId)
    {
        var local = CadenceSchedule.ToLocal(utc, zone);
        return new SeriesTimeDto(
            DateTime.SpecifyKind(utc, DateTimeKind.Utc),
            $"{Short(local.DayOfWeek)} {DayMonth(DateOnly.FromDateTime(local))}, "
                + $"{local.ToString("HH:mm", CultureInfo.InvariantCulture)} {ZoneLabel(timeZoneId)}",
            local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            local.ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    private static string Short(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "Mon",
        DayOfWeek.Tuesday => "Tue",
        DayOfWeek.Wednesday => "Wed",
        DayOfWeek.Thursday => "Thu",
        DayOfWeek.Friday => "Fri",
        DayOfWeek.Saturday => "Sat",
        _ => "Sun",
    };

    private static string JoinAnd(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }

    /// <summary>"HH:mm" to a time; null if it is not one.</summary>
    public static TimeOnly? ParseTime(string? value) =>
        TimeOnly.TryParseExact(value?.Trim(), new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var t) ? t : null;

    /// <summary>"yyyy-MM-dd" to a day; null if it is not one.</summary>
    public static DateOnly? ParseDay(string? value) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;
}
