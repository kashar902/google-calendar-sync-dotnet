using Google;
using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using GoogleCalendarSync.Configuration;
using System.Net;

namespace GoogleCalendarSync.Services;

/// <summary>
/// Thin wrapper around the Google Calendar API v3. Owns OAuth tokens for multiple employees and
/// exposes the list/insert/update/delete operations the sync engine needs.
/// </summary>
public sealed class GoogleCalendarService
{
    public const string DefaultCalendarId = "primary";

    private static readonly string[] Scopes =
    {
        CalendarService.Scope.Calendar,
        "openid",
        "email",
        "profile"
    };

    private readonly GoogleOptions _options;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly Dictionary<string, CalendarService> _services = new(StringComparer.OrdinalIgnoreCase);

    public GoogleCalendarService(GoogleOptions options)
    {
        _options = options;
    }

    public string CreateAuthorizationUrl(string redirectUri, string? loginHint = null)
    {
        var flow = CreateAuthorizationCodeFlow();
        var request = (GoogleAuthorizationCodeRequestUrl)flow.CreateAuthorizationCodeRequest(redirectUri);
        request.AccessType = "offline";
        request.Prompt = "consent";
        request.IncludeGrantedScopes = "true";
        request.LoginHint = loginHint;

        return request.Build().ToString();
    }

    public async Task<AuthenticatedEmployee> AuthenticateWithCodeAsync(
        string code,
        string redirectUri,
        CancellationToken ct = default)
    {
        var flow = CreateAuthorizationCodeFlow();
        var token = await flow.ExchangeCodeForTokenAsync("pending", code, redirectUri, ct);
        var profile = await ReadProfileAsync(token);
        var employeeEmail = NormalizeEmail(profile.Email);

        await flow.DataStore.StoreAsync(employeeEmail, token);
        await flow.DataStore.DeleteAsync<TokenResponse>("pending");

        await InitializeServiceAsync(employeeEmail, ct);

        return new AuthenticatedEmployee(
            employeeEmail,
            profile.Name,
            profile.EmailVerified,
            profile.HostedDomain);
    }

    public async Task<bool> IsAuthenticatedAsync(string employeeEmail, CancellationToken ct = default)
    {
        if (_services.ContainsKey(NormalizeEmail(employeeEmail)))
            return true;

        return await TryLoadServiceAsync(employeeEmail, ct);
    }

    public async Task<IReadOnlyList<string>> ListAuthenticatedEmployeesAsync(CancellationToken ct = default)
    {
        var tokenStore = new FileDataStore(_options.TokenStorePath, fullPath: true);
        var employees = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(_options.TokenStorePath))
        {
            foreach (var path in Directory.EnumerateFiles(
                _options.TokenStorePath,
                "Google.Apis.Auth.OAuth2.Responses.TokenResponse-*"))
            {
                var fileName = Path.GetFileName(path);
                var employeeEmail = Uri.UnescapeDataString(fileName["Google.Apis.Auth.OAuth2.Responses.TokenResponse-".Length..]);

                if (employeeEmail.Equals("pending", StringComparison.OrdinalIgnoreCase) ||
                    !employeeEmail.Contains('@', StringComparison.Ordinal))
                {
                    continue;
                }

                var token = await tokenStore.GetAsync<TokenResponse>(employeeEmail);
                if (token is not null)
                    employees.Add(employeeEmail);
            }
        }

        await _cacheLock.WaitAsync(ct);
        try
        {
            foreach (var employeeEmail in _services.Keys)
                employees.Add(employeeEmail);
        }
        finally
        {
            _cacheLock.Release();
        }

        return employees.ToList();
    }

    public async Task<ListResult> ListEventsAsync(
        string employeeEmail,
        string calendarId,
        string? syncToken,
        CancellationToken ct = default)
    {
        var service = await GetServiceAsync(employeeEmail, ct);
        var all = new List<Event>();
        string? pageToken = null;
        string? nextSyncToken = null;

        do
        {
            EventsResource.ListRequest request = service.Events.List(calendarId);
            request.ShowDeleted = true;
            request.SingleEvents = true;
            request.MaxResults = 250;
            request.PageToken = pageToken;

            if (!string.IsNullOrEmpty(syncToken))
            {
                request.SyncToken = syncToken;
            }
            else
            {
                request.TimeMinDateTimeOffset = DateTimeOffset.UtcNow.AddYears(-1);
            }

            Events page;
            try
            {
                page = await RetryHelper.ExecuteAsync(() => request.ExecuteAsync(ct), ct: ct);
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.Gone)
            {
                return new ListResult(Array.Empty<Event>(), null, SyncTokenExpired: true);
            }

            if (page.Items != null)
                all.AddRange(page.Items);

            pageToken = page.NextPageToken;
            nextSyncToken = page.NextSyncToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new ListResult(all, nextSyncToken, SyncTokenExpired: false);
    }

    public async Task<Event> InsertAsync(
        string employeeEmail,
        string calendarId,
        Event body,
        CancellationToken ct = default)
    {
        var service = await GetServiceAsync(employeeEmail, ct);
        return await RetryHelper.ExecuteAsync(() => service.Events.Insert(body, calendarId).ExecuteAsync(ct), ct: ct);
    }

    public async Task<Event> UpdateAsync(
        string employeeEmail,
        string calendarId,
        string eventId,
        Event body,
        CancellationToken ct = default)
    {
        var service = await GetServiceAsync(employeeEmail, ct);
        return await RetryHelper.ExecuteAsync(() => service.Events.Update(body, calendarId, eventId).ExecuteAsync(ct), ct: ct);
    }

    public async Task DeleteAsync(
        string employeeEmail,
        string calendarId,
        string eventId,
        CancellationToken ct = default)
    {
        var service = await GetServiceAsync(employeeEmail, ct);

        try
        {
            await RetryHelper.ExecuteAsync(
                () => service.Events.Delete(calendarId, eventId).ExecuteAsync(ct), ct: ct);
        }
        catch (GoogleApiException ex) when (
            ex.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // Already deleted on the server.
        }
    }

    private async Task<CalendarService> GetServiceAsync(string employeeEmail, CancellationToken ct)
    {
        var normalizedEmail = NormalizeEmail(employeeEmail);

        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_services.TryGetValue(normalizedEmail, out var cached))
                return cached;
        }
        finally
        {
            _cacheLock.Release();
        }

        if (await TryLoadServiceAsync(normalizedEmail, ct))
        {
            await _cacheLock.WaitAsync(ct);
            try
            {
                return _services[normalizedEmail];
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        throw new InvalidOperationException(
            $"Google is not authenticated for '{normalizedEmail}'. Open /auth/google?loginHint={Uri.EscapeDataString(normalizedEmail)} first.");
    }

    private async Task<bool> TryLoadServiceAsync(string employeeEmail, CancellationToken ct)
    {
        var normalizedEmail = NormalizeEmail(employeeEmail);
        var flow = CreateAuthorizationCodeFlow();
        var token = await flow.LoadTokenAsync(normalizedEmail, ct);

        if (token is null)
            return false;

        InitializeService(normalizedEmail, new UserCredential(flow, normalizedEmail, token));
        return true;
    }

    private async Task InitializeServiceAsync(string employeeEmail, CancellationToken ct)
    {
        var loaded = await TryLoadServiceAsync(employeeEmail, ct);

        if (!loaded)
            throw new InvalidOperationException($"Could not load Google token for '{employeeEmail}'.");
    }

    private void InitializeService(string employeeEmail, UserCredential credential)
    {
        var service = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = _options.ApplicationName
        });

        _cacheLock.Wait();
        try
        {
            _services[NormalizeEmail(employeeEmail)] = service;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private GoogleAuthorizationCodeFlow CreateAuthorizationCodeFlow() =>
        new(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = ResolveClientSecrets(),
            Scopes = Scopes,
            DataStore = new FileDataStore(_options.TokenStorePath, fullPath: true)
        });

    private async Task<GoogleJsonWebSignature.Payload> ReadProfileAsync(TokenResponse token)
    {
        if (string.IsNullOrWhiteSpace(token.IdToken))
            throw new InvalidOperationException("Google did not return an id_token. Make sure openid and email scopes are enabled.");

        var clientId = ResolveClientSecrets().ClientId;
        var payload = await GoogleJsonWebSignature.ValidateAsync(
            token.IdToken,
            new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { clientId }
            });

        if (string.IsNullOrWhiteSpace(payload.Email))
            throw new InvalidOperationException("Google did not return an email for this account.");

        return payload;
    }

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

    private static string NormalizeEmail(string employeeEmail) =>
        employeeEmail.Trim().ToLowerInvariant();

    public sealed record AuthenticatedEmployee(
        string Email,
        string? Name,
        bool EmailVerified,
        string? HostedDomain);

    public sealed record ListResult(
        IReadOnlyList<Event> Events,
        string? NextSyncToken,
        bool SyncTokenExpired);
}
