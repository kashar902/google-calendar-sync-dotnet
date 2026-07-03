using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using GoogleCalendarSync.Configuration;
using System.Net;

namespace GoogleCalendarSync.Services;

/// <summary>
/// Thin wrapper around the Google Calendar API v3. Owns authentication and exposes the
/// list/insert/update/delete operations the sync engine needs. All network calls go through
/// <see cref="RetryHelper"/> for backoff on rate-limit / transient errors.
/// </summary>
public sealed class GoogleCalendarService
{
    private const string UserId = "user";

    private readonly GoogleOptions _options;
    private CalendarService? _service;

    public GoogleCalendarService(GoogleOptions options)
    {
        _options = options;
    }

    public bool IsAuthenticated => _service is not null;

    /// <summary>
    /// Loads a previously granted OAuth token from disk, if one exists.
    /// </summary>
    public async Task<bool> TryAuthenticateFromStoreAsync(CancellationToken ct = default)
    {
        var flow = CreateAuthorizationCodeFlow();
        var token = await flow.LoadTokenAsync(UserId, ct);

        if (token is null)
            return false;

        InitializeService(new UserCredential(flow, UserId, token));
        return true;
    }

    public string CreateAuthorizationUrl(string redirectUri)
    {
        var flow = CreateAuthorizationCodeFlow();
        return flow.CreateAuthorizationCodeRequest(redirectUri).Build().ToString();
    }

    public async Task AuthenticateWithCodeAsync(string code, string redirectUri, CancellationToken ct = default)
    {
        var flow = CreateAuthorizationCodeFlow();
        var token = await flow.ExchangeCodeForTokenAsync(UserId, code, redirectUri, ct);
        InitializeService(new UserCredential(flow, UserId, token));
    }

    private GoogleAuthorizationCodeFlow CreateAuthorizationCodeFlow() =>
        new(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = ResolveClientSecrets(),
            Scopes = new[] { CalendarService.Scope.Calendar },
            DataStore = new FileDataStore(_options.TokenStorePath, fullPath: true)
        });

    private void InitializeService(UserCredential credential)
    {
        _service = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = _options.ApplicationName
        });
    }

    private CalendarService Service =>
        _service ?? throw new InvalidOperationException("Call AuthenticateAsync() first.");

    /// <summary>
    /// Resolves the OAuth client id/secret. A downloaded credentials JSON (see
    /// <see cref="GoogleOptions.CredentialsPath"/>) wins if present — this handles both the
    /// "installed" (Desktop app) and "web" JSON shapes via the library parser — otherwise we fall
    /// back to explicit <c>ClientId</c>/<c>ClientSecret</c> from appsettings.json / environment vars.
    /// </summary>
    private ClientSecrets ResolveClientSecrets()
    {
        var credentialsFile = ResolveCredentialsFile();
        if (credentialsFile is not null)
        {
            using var stream = File.OpenRead(credentialsFile);
            var secrets = GoogleClientSecrets.FromStream(stream).Secrets;
            Console.WriteLine($"Using OAuth client from credentials file: {credentialsFile}");
            return secrets;
        }

        if (!string.IsNullOrWhiteSpace(_options.ClientId) &&
            !_options.ClientId.StartsWith("YOUR_", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            return new ClientSecrets
            {
                ClientId = _options.ClientId,
                ClientSecret = _options.ClientSecret
            };
        }

        throw new InvalidOperationException(
            "No Google OAuth credentials found. Either drop the downloaded OAuth client JSON into " +
            $"the '{_options.CredentialsPath}' folder, or set Google:ClientId / Google:ClientSecret " +
            "in appsettings.json (or the Google__ClientId / Google__ClientSecret environment variables).");
    }

    /// <summary>
    /// Finds a credentials JSON file. <see cref="GoogleOptions.CredentialsPath"/> may be a file or a
    /// folder (first *.json wins). Relative paths are probed against both the current working
    /// directory and the app base directory so it works from `dotnet run` and a published exe.
    /// </summary>
    private string? ResolveCredentialsFile()
    {
        var configured = string.IsNullOrWhiteSpace(_options.CredentialsPath)
            ? "credentials"
            : _options.CredentialsPath;

        var roots = Path.IsPathRooted(configured)
            ? new[] { configured }
            : new[]
            {
                Path.Combine(Environment.CurrentDirectory, configured),
                Path.Combine(AppContext.BaseDirectory, configured)
            };

        foreach (var candidate in roots)
        {
            if (File.Exists(candidate))
                return candidate;

            if (Directory.Exists(candidate))
            {
                var json = Directory.EnumerateFiles(candidate, "*.json").FirstOrDefault();
                if (json is not null)
                    return json;
            }
        }

        return null;
    }

    /// <summary>
    /// Result of an incremental (or full) list. <see cref="Events"/> contains every changed
    /// event page-by-page; <see cref="NextSyncToken"/> is the token to persist for the next run;
    /// <see cref="SyncTokenExpired"/> is true when the supplied token was rejected (HTTP 410) and
    /// the caller must fall back to a full sync.
    /// </summary>
    public sealed record ListResult(
        IReadOnlyList<Event> Events,
        string? NextSyncToken,
        bool SyncTokenExpired);

    /// <summary>
    /// Pulls changes from Google. Pass the previously stored <paramref name="syncToken"/> for an
    /// incremental pull, or null for a full sync. If Google rejects the token with 410 Gone, this
    /// returns <see cref="ListResult.SyncTokenExpired"/> = true so the caller can retry full.
    /// </summary>
    public async Task<ListResult> ListEventsAsync(string? syncToken, CancellationToken ct = default)
    {
        var all = new List<Event>();
        string? pageToken = null;
        string? nextSyncToken = null;

        do
        {
            EventsResource.ListRequest request = Service.Events.List(_options.CalendarId);
            request.ShowDeleted = true;              // needed to observe cancelled/deleted events
            request.SingleEvents = true;             // expand recurring events into instances
            request.MaxResults = 250;
            request.PageToken = pageToken;

            if (!string.IsNullOrEmpty(syncToken))
            {
                request.SyncToken = syncToken;
            }
            else
            {
                // Full sync: bound the window so we don't drag in ancient history.
                request.TimeMinDateTimeOffset = DateTimeOffset.UtcNow.AddYears(-1);
            }

            Events page;
            try
            {
                page = await RetryHelper.ExecuteAsync(() => request.ExecuteAsync(ct), ct: ct);
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.Gone)
            {
                // 410: the sync token is no longer valid. Signal the caller to do a full sync.
                return new ListResult(Array.Empty<Event>(), null, SyncTokenExpired: true);
            }

            if (page.Items != null)
                all.AddRange(page.Items);

            pageToken = page.NextPageToken;
            nextSyncToken = page.NextSyncToken; // only present on the last page
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new ListResult(all, nextSyncToken, SyncTokenExpired: false);
    }

    public Task<Event> InsertAsync(Event body, CancellationToken ct = default) =>
        RetryHelper.ExecuteAsync(() => Service.Events.Insert(body, _options.CalendarId).ExecuteAsync(ct), ct: ct);

    public Task<Event> UpdateAsync(string eventId, Event body, CancellationToken ct = default) =>
        RetryHelper.ExecuteAsync(() => Service.Events.Update(body, _options.CalendarId, eventId).ExecuteAsync(ct), ct: ct);

    /// <summary>
    /// Deletes an event. A 404/410 (already gone) is treated as success so delete is idempotent.
    /// </summary>
    public async Task DeleteAsync(string eventId, CancellationToken ct = default)
    {
        try
        {
            await RetryHelper.ExecuteAsync(
                () => Service.Events.Delete(_options.CalendarId, eventId).ExecuteAsync(ct), ct: ct);
        }
        catch (GoogleApiException ex) when (
            ex.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // Already deleted on the server — nothing to do.
        }
    }
}
