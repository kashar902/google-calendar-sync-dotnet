using System.ComponentModel.DataAnnotations;

namespace GoogleCalendarSync.Data;

/// <summary>
/// A calendar event as stored in the local SQLite store. Each row maps to at most one
/// Google event via <see cref="GoogleEventId"/>. The bookkeeping fields
/// (<see cref="ETag"/>, <see cref="GoogleUpdated"/>, <see cref="LastModified"/>,
/// <see cref="IsDirty"/>) drive incremental sync and conflict detection.
/// </summary>
public class LocalEvent
{
    /// <summary>Local primary key (independent of Google).</summary>
    [Key]
    public int Id { get; set; }

    /// <summary>The Google Calendar event id. Null until the event has been pushed to Google.</summary>
    public string? GoogleEventId { get; set; }

    // --- Event payload -----------------------------------------------------
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? Location { get; set; }

    /// <summary>Start time (UTC). For all-day events this is the date at 00:00 UTC.</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>End time (UTC). For all-day events this is the exclusive end date at 00:00 UTC.</summary>
    public DateTime EndUtc { get; set; }

    public bool IsAllDay { get; set; }

    // --- Sync bookkeeping --------------------------------------------------

    /// <summary>Soft-delete flag. Deletions are tombstoned so both sides can converge.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// True when this row has local changes that have not yet been pushed to Google.
    /// Set whenever the app mutates the event locally; cleared after a successful push.
    /// </summary>
    public bool IsDirty { get; set; }

    /// <summary>Last time this row was modified locally (UTC). Used for conflict resolution.</summary>
    public DateTime LastModified { get; set; }

    /// <summary>Google's <c>updated</c> timestamp from the last successful pull (UTC). Used for conflict resolution.</summary>
    public DateTime? GoogleUpdated { get; set; }

    /// <summary>Google's ETag from the last pull. Lets us detect server-side changes cheaply.</summary>
    public string? ETag { get; set; }
}
