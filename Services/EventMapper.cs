using Google.Apis.Calendar.v3.Data;
using GoogleCalendarSync.Data;

namespace GoogleCalendarSync.Services;

/// <summary>
/// Translates between the Google <see cref="Event"/> shape and our <see cref="LocalEvent"/> rows,
/// including the all-day vs timed distinction Google encodes in <see cref="EventDateTime"/>.
/// </summary>
public static class EventMapper
{
    /// <summary>Copies Google's payload fields onto a local row (does not touch sync bookkeeping).</summary>
    public static void ApplyGoogleToLocal(Event g, LocalEvent local)
    {
        local.Summary = g.Summary;
        local.Description = g.Description;
        local.Location = g.Location;

        (local.StartUtc, local.EndUtc, local.IsAllDay) = ReadTimes(g);
    }

    /// <summary>Builds the Google request body from a local row's payload fields.</summary>
    public static Event ToGoogleEvent(LocalEvent local)
    {
        var g = new Event
        {
            Summary = local.Summary,
            Description = local.Description,
            Location = local.Location
        };

        if (local.IsAllDay)
        {
            g.Start = new EventDateTime { Date = local.StartUtc.ToString("yyyy-MM-dd") };
            g.End = new EventDateTime { Date = local.EndUtc.ToString("yyyy-MM-dd") };
        }
        else
        {
            g.Start = new EventDateTime { DateTimeDateTimeOffset = new DateTimeOffset(local.StartUtc, TimeSpan.Zero) };
            g.End = new EventDateTime { DateTimeDateTimeOffset = new DateTimeOffset(local.EndUtc, TimeSpan.Zero) };
        }

        return g;
    }

    private static (DateTime start, DateTime end, bool allDay) ReadTimes(Event g)
    {
        // All-day events carry Date ("yyyy-MM-dd") instead of DateTime.
        if (!string.IsNullOrEmpty(g.Start?.Date))
        {
            var start = DateTime.SpecifyKind(DateTime.Parse(g.Start.Date), DateTimeKind.Utc);
            var end = !string.IsNullOrEmpty(g.End?.Date)
                ? DateTime.SpecifyKind(DateTime.Parse(g.End.Date), DateTimeKind.Utc)
                : start.AddDays(1);
            return (start, end, true);
        }

        var startUtc = g.Start?.DateTimeDateTimeOffset?.UtcDateTime ?? DateTime.UtcNow;
        var endUtc = g.End?.DateTimeDateTimeOffset?.UtcDateTime ?? startUtc.AddHours(1);
        return (startUtc, endUtc, false);
    }

    /// <summary>Reads Google's <c>updated</c> timestamp as UTC (used for conflict resolution).</summary>
    public static DateTime? GetUpdatedUtc(Event g) => g.UpdatedDateTimeOffset?.UtcDateTime;
}
