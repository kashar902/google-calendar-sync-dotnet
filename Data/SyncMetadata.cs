using System.ComponentModel.DataAnnotations;

namespace GoogleCalendarSync.Data;

/// <summary>
/// Simple key/value row used to persist sync-wide state such as the Google
/// <c>nextSyncToken</c> and the timestamp of the last successful sync.
/// </summary>
public class SyncMetadata
{
    [Key]
    public string Key { get; set; } = string.Empty;

    public string? Value { get; set; }

    public static class Keys
    {
        public const string NextSyncToken = "NextSyncToken";
        public const string LastSyncUtc = "LastSyncUtc";
    }
}
