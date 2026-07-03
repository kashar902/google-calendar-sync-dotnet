using GoogleCalendarSync.Configuration;
using GoogleCalendarSync.Data;
using Microsoft.EntityFrameworkCore;

namespace GoogleCalendarSync.Services;

/// <summary>
/// Orchestrates a full two-way sync cycle:
///   1. Pull  (Google → local) using an incremental sync token, with conflict handling.
///   2. Push  (local → Google) for rows the app changed locally.
/// The order is deliberate: we pull first so that push-time conflict decisions are made against
/// the freshest server state we have.
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
            $"Pull  → created {PulledCreated}, updated {PulledUpdated}, deleted {PulledDeleted}\n" +
            $"Push  → created {PushedCreated}, updated {PushedUpdated}, deleted {PushedDeleted}\n" +
            $"Conflicts resolved: {Conflicts}";
    }

    public async Task<SyncSummary> RunAsync(CancellationToken ct = default)
    {
        var summary = new SyncSummary();

        await PullAsync(summary, ct);
        await PushAsync(summary, ct);

        await _db.SetMetadataAsync(
            SyncMetadata.Keys.LastSyncUtc,
            DateTime.UtcNow.ToString("O"), ct);
        await _db.SaveChangesAsync(ct);

        return summary;
    }

    // ======================================================================
    //  PULL: Google → local
    // ======================================================================
    private async Task PullAsync(SyncSummary summary, CancellationToken ct)
    {
        var syncToken = await _db.GetMetadataAsync(SyncMetadata.Keys.NextSyncToken, ct);

        var result = await _google.ListEventsAsync(syncToken, ct);

        // Token rejected (410): drop it and do a fresh full sync.
        if (result.SyncTokenExpired)
        {
            Console.WriteLine("  [pull] sync token expired — falling back to a full sync.");
            await _db.SetMetadataAsync(SyncMetadata.Keys.NextSyncToken, null, ct);
            result = await _google.ListEventsAsync(null, ct);
        }

        foreach (var g in result.Events)
        {
            var local = await _db.Events
                .FirstOrDefaultAsync(e => e.GoogleEventId == g.Id, ct);

            var isCancelled = string.Equals(g.Status, "cancelled", StringComparison.OrdinalIgnoreCase);

            if (local is null)
            {
                if (isCancelled)
                    continue; // deleted event we never knew about — nothing to do

                local = new LocalEvent { GoogleEventId = g.Id };
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
                    local.IsDirty = false; // deletion came FROM Google; no need to push back
                    local.GoogleUpdated = EventMapper.GetUpdatedUtc(g);
                    summary.PulledDeleted++;
                }
                continue;
            }

            // ----------------------------------------------------------------
            //  CONFLICT RESOLUTION  (the trickiest part of two-way sync)
            //  The row changed on the server. If it ALSO has unpushed local edits
            //  (IsDirty), we must decide which side wins based on the configured
            //  strategy. If it is not dirty, Google's change simply applies.
            // ----------------------------------------------------------------
            if (local.IsDirty)
            {
                summary.Conflicts++;
                var googleUpdated = EventMapper.GetUpdatedUtc(g) ?? DateTime.MinValue;
                bool googleWins = ResolveGoogleWins(local, googleUpdated);

                Console.WriteLine(
                    $"  [conflict] event '{local.Summary}' changed on both sides — " +
                    $"{(googleWins ? "Google" : "local")} wins ({_options.ConflictStrategy}).");

                if (googleWins)
                {
                    EventMapper.ApplyGoogleToLocal(g, local);
                    local.ETag = g.ETag;
                    local.GoogleUpdated = googleUpdated;
                    local.LastModified = googleUpdated;
                    local.IsDirty = false; // discard the local edit
                    summary.PulledUpdated++;
                }
                else
                {
                    // Local wins: keep IsDirty so the push phase overwrites Google.
                    // Record what we saw so the push doesn't re-flag a conflict.
                    local.GoogleUpdated = googleUpdated;
                    local.ETag = g.ETag;
                }
                continue;
            }

            // No local edits — apply the server change straight through.
            EventMapper.ApplyGoogleToLocal(g, local);
            local.ETag = g.ETag;
            local.GoogleUpdated = EventMapper.GetUpdatedUtc(g);
            local.LastModified = local.GoogleUpdated ?? DateTime.UtcNow;
            summary.PulledUpdated++;
        }

        // Persist the new token so the next run is incremental. Only present after the last page.
        if (!string.IsNullOrEmpty(result.NextSyncToken))
            await _db.SetMetadataAsync(SyncMetadata.Keys.NextSyncToken, result.NextSyncToken, ct);

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Decides whether Google's copy should win a conflict, per the configured strategy.
    /// </summary>
    private bool ResolveGoogleWins(LocalEvent local, DateTime googleUpdated) =>
        _options.ConflictStrategy switch
        {
            ConflictStrategy.GoogleWins => true,
            ConflictStrategy.LocalWins => false,
            // LastWriteWins: newer timestamp wins; ties go to Google to stay convergent.
            _ => googleUpdated >= local.LastModified
        };

    // ======================================================================
    //  PUSH: local → Google
    // ======================================================================
    private async Task PushAsync(SyncSummary summary, CancellationToken ct)
    {
        var dirty = await _db.Events
            .Where(e => e.IsDirty)
            .ToListAsync(ct);

        foreach (var local in dirty)
        {
            // Deleted locally --------------------------------------------------
            if (local.IsDeleted)
            {
                if (!string.IsNullOrEmpty(local.GoogleEventId))
                {
                    await _google.DeleteAsync(local.GoogleEventId, ct);
                    summary.PushedDeleted++;
                }
                _db.Events.Remove(local); // tombstone consumed — drop the row
                continue;
            }

            // Created locally (never pushed) ----------------------------------
            if (string.IsNullOrEmpty(local.GoogleEventId))
            {
                var created = await _google.InsertAsync(EventMapper.ToGoogleEvent(local), ct);
                local.GoogleEventId = created.Id;
                local.ETag = created.ETag;
                local.GoogleUpdated = EventMapper.GetUpdatedUtc(created);
                local.IsDirty = false;
                summary.PushedCreated++;
                continue;
            }

            // Updated locally -------------------------------------------------
            var body = EventMapper.ToGoogleEvent(local);
            var updated = await _google.UpdateAsync(local.GoogleEventId, body, ct);
            local.ETag = updated.ETag;
            local.GoogleUpdated = EventMapper.GetUpdatedUtc(updated);
            local.IsDirty = false;
            summary.PushedUpdated++;
        }

        await _db.SaveChangesAsync(ct);
    }
}
