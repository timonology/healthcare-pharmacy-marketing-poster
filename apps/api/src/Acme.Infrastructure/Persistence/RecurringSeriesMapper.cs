using System.Globalization;
using System.Reflection;
using Acme.Domain.Social;

namespace Acme.Infrastructure.Persistence;

/// <summary>Document ↔ domain for recurring series. Same reflection approach as <see cref="SocialMapper"/>.</summary>
internal static class RecurringSeriesMapper
{
    private static readonly ConstructorInfo Ctor =
        typeof(RecurringSeries).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("RecurringSeries parameterless ctor missing.");

    private const string DayFormat = "yyyy-MM-dd";

    public static RecurringSeries Hydrate(RecurringSeriesDocument d)
    {
        var s = (RecurringSeries)Ctor.Invoke(null);
        Set(s, nameof(RecurringSeries.Id), d.Id);
        Set(s, nameof(RecurringSeries.OwnerId), d.OwnerId);
        Set(s, nameof(RecurringSeries.Name), d.Name ?? string.Empty);
        Set(s, nameof(RecurringSeries.SourceKind), (PublishSourceKind)d.SourceKind);
        Set(s, nameof(RecurringSeries.SourceId), d.SourceId);
        Set(s, nameof(RecurringSeries.SourceName), d.SourceName ?? string.Empty);
        Set(s, nameof(RecurringSeries.Caption), d.Caption ?? string.Empty);
        Set(s, nameof(RecurringSeries.Cadence), new Cadence(
            (CadenceKind)d.CadenceKind,
            TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(Math.Clamp(d.TimeOfDayMinutes, 0, 24 * 60 - 1))),
            (d.Days ?? new()).Select(x => (DayOfWeek)x).ToList(),
            d.IntervalDays < 1 ? 1 : d.IntervalDays,
            (d.Dates ?? new()).Select(ParseDay).OfType<DateOnly>().ToList()));
        Set(s, nameof(RecurringSeries.TimeZoneId), d.TimeZoneId ?? "Europe/London");
        Set(s, nameof(RecurringSeries.StartDate), ParseDay(d.StartDate) ?? DateOnly.FromDateTime(d.CreatedAtUtc));
        Set(s, nameof(RecurringSeries.EndDate), ParseDay(d.EndDate));
        Set(s, nameof(RecurringSeries.Status), (SeriesStatus)d.Status);
        Set(s, nameof(RecurringSeries.StatusReason), d.StatusReason);
        Set(s, nameof(RecurringSeries.PausedBySystem), d.PausedBySystem);
        Set(s, nameof(RecurringSeries.Notify), (SeriesNotify)d.Notify);
        Set(s, nameof(RecurringSeries.NextOccurrenceUtc), d.NextOccurrenceUtc);
        Set(s, nameof(RecurringSeries.LastOccurrenceUtc), d.LastOccurrenceUtc);
        Set(s, nameof(RecurringSeries.MissedCount), d.MissedCount);
        Set(s, nameof(RecurringSeries.LastMissedAtUtc), d.LastMissedAtUtc);
        Set(s, nameof(RecurringSeries.Revision), d.Revision);
        Set(s, nameof(RecurringSeries.CreatedAtUtc), d.CreatedAtUtc);
        Set(s, nameof(RecurringSeries.UpdatedAtUtc), d.UpdatedAtUtc);

        var destinations = (d.Destinations ?? new())
            .Select(x => new SeriesDestination(x.ConnectionId, (SocialPlatform)x.Platform))
            .ToList();
        var field = typeof(RecurringSeries).GetField("_destinations", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RecurringSeries._destinations not found.");
        field.SetValue(s, destinations);

        return s;
    }

    public static RecurringSeriesDocument ToDocument(RecurringSeries s) => new()
    {
        Id = s.Id,
        OwnerId = s.OwnerId,
        Name = s.Name,
        SourceKind = (int)s.SourceKind,
        SourceId = s.SourceId,
        SourceName = s.SourceName,
        Caption = s.Caption,
        Destinations = s.Destinations
            .Select(x => new SeriesDestinationDocument { ConnectionId = x.ConnectionId, Platform = (int)x.Platform })
            .ToList(),
        CadenceKind = (int)s.Cadence.Kind,
        TimeOfDayMinutes = s.Cadence.TimeOfDay.Hour * 60 + s.Cadence.TimeOfDay.Minute,
        Days = (s.Cadence.Days ?? Array.Empty<DayOfWeek>()).Select(x => (int)x).ToList(),
        IntervalDays = s.Cadence.IntervalDays,
        Dates = (s.Cadence.Dates ?? Array.Empty<DateOnly>())
            .Select(x => x.ToString(DayFormat, CultureInfo.InvariantCulture)).ToList(),
        TimeZoneId = s.TimeZoneId,
        StartDate = s.StartDate.ToString(DayFormat, CultureInfo.InvariantCulture),
        EndDate = s.EndDate?.ToString(DayFormat, CultureInfo.InvariantCulture),
        Status = (int)s.Status,
        StatusReason = s.StatusReason,
        PausedBySystem = s.PausedBySystem,
        Notify = (int)s.Notify,
        NextOccurrenceUtc = s.NextOccurrenceUtc,
        LastOccurrenceUtc = s.LastOccurrenceUtc,
        MissedCount = s.MissedCount,
        LastMissedAtUtc = s.LastMissedAtUtc,
        Revision = s.Revision,
        CreatedAtUtc = s.CreatedAtUtc,
        UpdatedAtUtc = s.UpdatedAtUtc,
    };

    private static DateOnly? ParseDay(string? value) =>
        DateOnly.TryParseExact(value, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    private static void Set(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(
            name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}
