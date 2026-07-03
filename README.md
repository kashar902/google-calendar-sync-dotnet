# Google Calendar Two-Way Sync API

A small **.NET 8 Web API** that performs two-way synchronization between a local
SQLite store and **Google Calendar** using the Google Calendar API v3.

- Pull from Google to local SQLite with incremental sync tokens.
- Push local creates, updates, and deletes back to Google Calendar.
- Resolve conflicts with `LastWriteWins`, `GoogleWins`, or `LocalWins`.
- Expose sync, auth, and local event operations through HTTP endpoints.
- Provide Swagger/OpenAPI documentation at `/swagger`.

---

## Do We Still Need A Desktop App OAuth Client?

No.

This project was converted from a console/desktop app to a Web API, so you should now use a
**Web application** OAuth client in Google Cloud Console.

Use this authorized redirect URI for local development:

```text
http://localhost:5000/auth/google/callback
```

If you run the API on a different port or deploy it to a domain, add the matching callback URL:

```text
https://your-domain.com/auth/google/callback
```

Desktop app credentials were useful for the old console flow. For the current Web API flow, create
or use a **Web application** credential instead.

---

## Project Layout

```text
GoogleCalendarSync.csproj
appsettings.json                  # config: OAuth, calendar id, sync settings
Program.cs                        # Web API host, Swagger, auth/sync/event endpoints
Configuration/AppSettings.cs      # strongly typed options + ConflictStrategy enum
Data/
  LocalEvent.cs                   # local event row + sync bookkeeping fields
  SyncMetadata.cs                 # key/value store for nextSyncToken, lastSync
  SyncDbContext.cs                # EF Core SQLite context
Services/
  GoogleCalendarService.cs        # Google Calendar API + OAuth code flow
  EventMapper.cs                  # Event <-> LocalEvent mapping
  RetryHelper.cs                  # exponential backoff for transient Google API errors
  SyncService.cs                  # pull, push, and conflict logic
```

---

## 1. Create Google OAuth Credentials

1. Go to the [Google Cloud Console](https://console.cloud.google.com/).
2. Create or select a project.
3. Enable **Google Calendar API**.
4. Configure the OAuth consent screen:
   - User type: **External** or **Internal**.
   - Add your account under **Test users** while the app is in testing.
   - Add the scope `https://www.googleapis.com/auth/calendar`.
5. Create credentials:
   - Go to **APIs & Services -> Credentials**.
   - Click **Create Credentials -> OAuth client ID**.
   - Application type: **Web application**.
   - Add this authorized redirect URI:

```text
http://localhost:5000/auth/google/callback
```

6. Download the JSON file.

Put the downloaded JSON file in the project `credentials/` folder.

Important: keep only one active `*.json` file in `credentials/`, or set `Google:CredentialsPath`
to the exact JSON file path. The app uses the first JSON file it finds in that folder.

---

## 2. Configure The App

The app can read OAuth credentials from either:

- a downloaded JSON file in `credentials/`, or
- `Google:ClientId` and `Google:ClientSecret` in config/environment variables.

Recommended local setup:

```json
{
  "Google": {
    "ClientId": "",
    "ClientSecret": "",
    "CredentialsPath": "credentials",
    "CalendarId": "primary",
    "ApplicationName": "GoogleCalendarSync",
    "TokenStorePath": "token_store"
  },
  "Sync": {
    "DatabasePath": "sync.db",
    "IntervalMinutes": 5,
    "ConflictStrategy": "LastWriteWins"
  }
}
```

Config keys:

| Key | Meaning |
| --- | --- |
| `Google:CredentialsPath` | Folder or exact JSON file path for the OAuth client JSON. |
| `Google:CalendarId` | Calendar to sync. `primary` means the signed-in user's main calendar. |
| `Google:TokenStorePath` | Folder where the OAuth refresh token is cached. |
| `Sync:DatabasePath` | SQLite database file for local event rows. |
| `Sync:IntervalMinutes` | Reserved sync interval setting. Manual sync currently runs through `POST /sync/run`. |
| `Sync:ConflictStrategy` | `LastWriteWins`, `GoogleWins`, or `LocalWins`. |

Do not commit real secrets. Prefer `appsettings.Local.json` or environment variables for private
values.

---

## 3. Run The Web API

```powershell
dotnet run -- --urls http://localhost:5000
```

Then open Swagger:

```text
http://localhost:5000/swagger
```

OpenAPI JSON:

```text
http://localhost:5000/swagger/v1/swagger.json
```

---

## 4. Authenticate With Google

Start the API first, then open:

```text
http://localhost:5000/auth/google
```

Sign in and approve access. Google redirects back to:

```text
http://localhost:5000/auth/google/callback
```

After success, the refresh token is cached under `token_store/`, so you usually only need to sign in
once.

Check auth status:

```http
GET /auth/status
```

---

## 5. API Endpoints

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/health` | Health check. |
| `GET` | `/auth/status` | Check whether Google auth is loaded. |
| `GET` | `/auth/google` | Start Google OAuth login. |
| `GET` | `/auth/google/callback` | Google OAuth redirect target. |
| `GET` | `/sync/status` | View sync/auth/database status. |
| `POST` | `/sync/run` | Run one pull-then-push sync cycle. |
| `GET` | `/events` | List local events. |
| `GET` | `/events/{id}` | Get one local event. |
| `POST` | `/events` | Create a local event and mark it dirty for push. |
| `PUT` | `/events/{id}` | Update a local event and mark it dirty for push. |
| `DELETE` | `/events/{id}` | Delete/tombstone a local event for push. |

Example create/update payload:

```json
{
  "summary": "Team standup",
  "description": "Daily check-in",
  "location": "Google Meet",
  "startUtc": "2026-07-03T10:00:00Z",
  "endUtc": "2026-07-03T10:30:00Z",
  "isAllDay": false
}
```

Run a sync:

```http
POST /sync/run
```

Then view local rows:

```http
GET /events
```

To include tombstoned/deleted rows:

```http
GET /events?includeDeleted=true
```

---

## Where Is The Data?

The local synced data is stored in SQLite.

For the default relative path, the API resolves it under the app output folder, commonly:

```text
bin/Debug/net8.0/sync.db
```

Open it with DB Browser for SQLite or a VS Code SQLite extension. The main tables are:

- `Events`
- `Metadata`

The actual calendar data also exists in Google Calendar for the configured `Google:CalendarId`.

---

## How Sync Works

Each sync cycle runs in this order:

1. Pull changes from Google Calendar into SQLite.
2. Resolve any Google-vs-local conflicts.
3. Push local dirty rows back to Google.
4. Store `LastSyncUtc` and the next incremental sync token.

Pull behavior:

- New Google event -> insert local row.
- Deleted/cancelled Google event -> tombstone local row.
- Existing clean local row -> apply Google changes.
- Existing dirty local row -> resolve conflict.

Push behavior:

- Local row without `GoogleEventId` -> insert into Google.
- Dirty deleted row -> delete from Google, then remove local row.
- Dirty existing row -> update Google.

---

## Notes

- Single-calendar sync only. Multi-calendar support would loop over calendar IDs.
- Recurring events are expanded into instances with `SingleEvents=true`.
- Secrets, tokens, credentials, logs, and SQLite files are git-ignored.
- If Google shows `redirect_uri_mismatch`, check that the URL in Google Cloud Console exactly
  matches the API URL and port you are running.
