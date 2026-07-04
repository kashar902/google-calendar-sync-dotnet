using GoogleCalendarSync.Configuration;
using GoogleCalendarSync.Data;
using Microsoft.EntityFrameworkCore;

namespace GoogleCalendarSync.Services;

/// <summary>
/// Orchestrates one employee's two-way sync cycle:
///   1. Pull Google changes into that employee's local rows.
///   2. Push that employee's dirty local rows back to Google.
/// </summary>
public sealed class SyncService
{
    private readonly GoogleCalendarService _google;
    private readonly SyncDbContext _db;
    private readonly SyncOptions _options;

    public SyncService(GoogleCalendarService google, SyncDbContext db, SyncOptions options)
    {
        _google = google;
        _db = db;
        _options = options;
    }

    public sealed class SyncSummary
    {
        public int PulledCreated, PulledUpdated, PulledDeleted;
        public int PushedCreated, PushedUpdated, PushedDeleted;
        public int Conflicts;

        public override string ToString() =>
            $"Pull  -> created {PulledCreated}, updated {PulledUpdated}, deleted {PulledDeleted}\n" +
            $"Push  -> created {PushedCreated}, updated {PushedUpdated}, deleted {PushedDeleted}\n" +
            $"Conflicts resolved: {Conflicts}";
    }

    public async Task<SyncSummary> RunAsync(
        string employeeEmail,
        string calendarId = GoogleCalendarService.DefaultCalendarId,
        CancellationToken ct = default)
    {
        employeeEmail = NormalizeEmail(employeeEmail);
        calendarId = NormalizeCalendarId(calendarId);

        var summary = new SyncSummary();

        await PullAsync(employeeEmail, calendarId, summary, ct);
        await PushAsync(employeeEmail, calendarId, summary, ct);

        await _db.SetMetadataAsync(
            employeeEmail,
            calendarId,
            SyncMetadata.Keys.LastSyncUtc,
            DateTime.UtcNow.ToString("O"), ct);
        await _db.SaveChangesAsync(ct);

        return summary;
    }

    private async Task PullAsync(
        string employeeEmail,
        string calendarId,
        SyncSummary summary,
        CancellationToken ct)
    {
        var syncToken = await _db.GetMetadataAsync(
            employeeEmail,
            calendarId,
            SyncMetadata.Keys.NextSyncToken,
            ct);

        var result = await _google.ListEventsAsync(employeeEmail, calendarId, syncToken, ct);

        if (result.SyncTokenExpired)
        {
            Console.WriteLine("  [pull] sync token expired; falling back to a full sync.");
            await _db.SetMetadataAsync(employeeEmail, calendarId, SyncMetadata.Keys.NextSyncToken, null, ct);
            result = await _google.ListEventsAsync(employeeEmail, calendarId, null, ct);
        }

        foreach (var g in result.Events)
        {
            var local = await _db.Events
                .FirstOrDefaultAsync(e =>
                    e.OwnerEmail == employeeEmail &&
                    e.CalendarId == calendarId &&
                    e.GoogleEventId == g.Id, ct);

            var isCancelled = string.Equals(g.Status, "cancelled", StringComparison.OrdinalIgnoreCase);

            if (local is null)
            {
                if (isCancelled)
                    continue;

                local = new LocalEvent
                {
                    GoogleEventId = g.Id,
                    OwnerEmail = employeeEmail,
                    CalendarId = calendarId
                };
                EventMapper.ApplyGoogleToLocal(g, local);
                local.ETag = g.ETag;
                local.GoogleUpdated = EventMapper.GetUpdatedUtc(g);
                local.LastModified = local.GoogleUpdated ?? DateTime.UtcNow;
                local.IsDirty = false;
                _db.Events.Add(local);
                summary.PulledCreated++;
                continue;
            }

            if (isCancelled)
            {
                if (!local.IsDeleted)
                {
                    local.IsDeleted = true;
                    local.IsDirty = false;
                    local.GoogleUpdated = EventMapper.GetUpdatedUtc(g);
                    summary.PulledDeleted++;
                }
                continue;
            }

            if (local.IsDirty)
            {
                summary.Conflicts++;
                var googleUpdated = EventMapper.GetUpdatedUtc(g) ?? DateTime.MinValue;
                bool googleWins = ResolveGoogleWins(local, googleUpdated);

                Console.WriteLine(
                    $"  [conflict] {employeeEmail} event '{local.Summary}' changed on both sides; " +
                    $"{(googleWins ? "Google" : "local")} wins ({_options.ConflictStrategy}).");

                if (googleWins)
                {
                    EventMapper.ApplyGoogleToLocal(g, local);
                    local.ETag = g.ETag;
                    local.GoogleUpdated = googleUpdated;
                    local.LastModified = googleUpdated;
                    local.IsDirty = false;
                    summary.PulledUpdated++;
                }
                else
                {
                    local.GoogleUpdated = googleUpdated;
                    local.ETag = g.ETag;
                }
                continue;
            }

            EventMapper.ApplyGoogleToLocal(g, local);
            local.ETag = g.ETag;
            local.GoogleUpdated = EventMapper.GetUpdatedUtc(g);
            local.LastModified = local.GoogleUpdated ?? DateTime.UtcNow;
            summary.PulledUpdated++;
        }

        if (!string.IsNullOrEmpty(result.NextSyncToken))
            await _db.SetMetadataAsync(
                employeeEmail,
                calendarId,
                SyncMetadata.Keys.NextSyncToken,
                result.NextSyncToken,
                ct);

        await _db.SaveChangesAsync(ct);
    }

    private bool ResolveGoogleWins(LocalEvent local, DateTime googleUpdated) =>
        _options.ConflictStrategy switch
        {
            ConflictStrategy.GoogleWins => true,
            ConflictStrategy.LocalWins => false,
            _ => googleUpdated >= local.LastModified
        };

    private async Task PushAsync(
        string employeeEmail,
        string calendarId,
        SyncSummary summary,
        CancellationToken ct)
    {
        var dirty = await _db.Events
            .Where(e =>
                e.OwnerEmail == employeeEmail &&
                e.CalendarId == calendarId &&
                e.IsDirty)
            .ToListAsync(ct);

        foreach (var local in dirty)
        {
            if (local.IsDeleted)
            {
                if (!string.IsNullOrEmpty(local.GoogleEventId))
                {
                    await _google.DeleteAsync(employeeEmail, calendarId, local.GoogleEventId, ct);
                    summary.PushedDeleted++;
                }
                _db.Events.Remove(local);
                continue;
            }

            if (string.IsNullOrEmpty(local.GoogleEventId))
            {
                var created = await _google.InsertAsync(employeeEmail, calendarId, EventMapper.ToGoogleEvent(local), ct);
                local.GoogleEventId = created.Id;
                local.ETag = created.ETag;
                local.GoogleUpdated = EventMapper.GetUpdatedUtc(created);
                local.IsDirty = false;
                summary.PushedCreated++;
                continue;
            }

            var updated = await _google.UpdateAsync(
                employeeEmail,
                calendarId,
                local.GoogleEventId,
                EventMapper.ToGoogleEvent(local),
                ct);
            local.ETag = updated.ETag;
            local.GoogleUpdated = EventMapper.GetUpdatedUtc(updated);
            local.IsDirty = false;
            summary.PushedUpdated++;
        }

        await _db.SaveChangesAsync(ct);
    }

    private static string NormalizeEmail(string employeeEmail) =>
        employeeEmail.Trim().ToLowerInvariant();

    private static string NormalizeCalendarId(string calendarId) =>
        string.IsNullOrWhiteSpace(calendarId) ? GoogleCalendarService.DefaultCalendarId : calendarId.Trim();
}
