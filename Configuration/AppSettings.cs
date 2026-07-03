namespace GoogleCalendarSync.Configuration;

/// <summary>
/// Strongly-typed view over the "Google" section of appsettings.json / environment variables.
/// </summary>
public sealed class GoogleOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Optional path to a downloaded Google OAuth client JSON (the file you get from the Cloud
    /// Console "Download JSON" button). May point at a single file or a folder — if it's a folder,
    /// the first *.json inside is used. When a credentials file is found it takes precedence over
    /// <see cref="ClientId"/> / <see cref="ClientSecret"/>, so you can just drop the file in.
    /// </summary>
    public string CredentialsPath { get; set; } = "credentials";

    /// <summary>Calendar to sync. "primary" targets the signed-in user's main calendar.</summary>
    public string CalendarId { get; set; } = "primary";

    public string ApplicationName { get; set; } = "GoogleCalendarSync";

    /// <summary>Folder where the OAuth refresh token is cached so we don't re-prompt every run.</summary>
    public string TokenStorePath { get; set; } = "token_store";
}

public enum ConflictStrategy
{
    /// <summary>Whichever side has the newer timestamp wins.</summary>
    LastWriteWins,

    /// <summary>Google's copy always overwrites local on conflict.</summary>
    GoogleWins,

    /// <summary>The local copy always overwrites Google on conflict.</summary>
    LocalWins
}

public sealed class SyncOptions
{
    public string DatabasePath { get; set; } = "sync.db";
    public int IntervalMinutes { get; set; } = 5;
    public ConflictStrategy ConflictStrategy { get; set; } = ConflictStrategy.LastWriteWins;
}
