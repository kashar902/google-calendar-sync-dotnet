using GoogleCalendarSync.Configuration;
using GoogleCalendarSync.Data;
using GoogleCalendarSync.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);

var googleOptions = builder.Configuration.GetSection("Google").Get<GoogleOptions>() ?? new GoogleOptions();
var syncOptions = builder.Configuration.GetSection("Sync").Get<SyncOptions>() ?? new SyncOptions();
syncOptions.DatabasePath = ResolveDatabasePath(syncOptions.DatabasePath);

var syncGate = new SemaphoreSlim(1, 1);

builder.Services.AddSingleton(googleOptions);
builder.Services.AddSingleton(syncOptions);
builder.Services.AddSingleton(syncGate);
builder.Services.AddSingleton(sp => new GoogleCalendarService(sp.GetRequiredService<GoogleOptions>()));
builder.Services.AddScoped(sp => new SyncDbContext(sp.GetRequiredService<SyncOptions>().DatabasePath));
builder.Services.AddScoped<SyncService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "GoogleCalendarSync API",
        Version = "v1",
        Description = "Web API for authenticating with Google Calendar, running two-way sync, and managing local calendar events."
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "GoogleCalendarSync API v1");
    options.RoutePrefix = "swagger";
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services
        .GetRequiredService<IServer>()
        .Features
        .Get<IServerAddressesFeature>()?
        .Addresses;

    if (addresses is null)
        return;

    foreach (var address in addresses)
        Console.WriteLine($"Web API running on {address}");
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SyncDbContext>();
    await db.Database.EnsureCreatedAsync();
}

Console.WriteLine($"SQLite database: {syncOptions.DatabasePath}");

var google = app.Services.GetRequiredService<GoogleCalendarService>();
var restoredCredential = await google.TryAuthenticateFromStoreAsync();

Console.WriteLine(restoredCredential
    ? "Google authentication loaded from token store."
    : "Google authentication required. Open /auth/google after the API starts.");

app.MapGet("/", () => Results.Ok(new
{
    name = "GoogleCalendarSync API",
    endpoints = new[]
    {
        "GET /health",
        "GET /auth/status",
        "GET /auth/google",
        "GET /auth/google/callback",
        "GET /swagger",
        "GET /swagger/v1/swagger.json",
        "GET /sync/status",
        "POST /sync/run",
        "GET /events",
        "GET /events/{id}",
        "POST /events",
        "PUT /events/{id}",
        "DELETE /events/{id}"
    }
}))
.WithName("GetApiIndex")
.WithTags("System");

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("GetHealth")
    .WithTags("System");

app.MapGet("/auth/status", (GoogleCalendarService googleService) =>
    Results.Ok(new { isAuthenticated = googleService.IsAuthenticated }))
    .WithName("GetAuthStatus")
    .WithTags("Authentication");

app.MapGet("/auth/google", (HttpRequest request, GoogleCalendarService googleService) =>
{
    var redirectUri = BuildGoogleRedirectUri(request);
    var authorizationUrl = googleService.CreateAuthorizationUrl(redirectUri);
    return Results.Redirect(authorizationUrl);
})
    .WithName("StartGoogleAuth")
    .WithTags("Authentication");

app.MapGet("/auth/google/callback", async (
    string? code,
    string? error,
    HttpRequest request,
    GoogleCalendarService googleService,
    CancellationToken ct) =>
{
    if (!string.IsNullOrWhiteSpace(error))
        return Results.BadRequest(new { error });

    if (string.IsNullOrWhiteSpace(code))
        return Results.BadRequest(new { error = "Missing authorization code." });

    await googleService.AuthenticateWithCodeAsync(code, BuildGoogleRedirectUri(request), ct);

    return Results.Content(
        "Google Calendar authentication complete. You can close this tab and use the API.",
        "text/plain");
})
    .WithName("CompleteGoogleAuth")
    .WithTags("Authentication");

app.MapGet("/sync/status", async (
    SyncDbContext db,
    SyncOptions options,
    GoogleCalendarService googleService,
    SemaphoreSlim gate,
    CancellationToken ct) =>
{
    var lastSyncUtc = await db.GetMetadataAsync(SyncMetadata.Keys.LastSyncUtc, ct);
    var hasSyncToken = !string.IsNullOrEmpty(await db.GetMetadataAsync(SyncMetadata.Keys.NextSyncToken, ct));

    return Results.Ok(new
    {
        isRunning = gate.CurrentCount == 0,
        isAuthenticated = googleService.IsAuthenticated,
        lastSyncUtc,
        hasSyncToken,
        intervalMinutes = options.IntervalMinutes,
        conflictStrategy = options.ConflictStrategy.ToString(),
        databasePath = options.DatabasePath
    });
})
    .WithName("GetSyncStatus")
    .WithTags("Sync");

app.MapPost("/sync/run", async (
    SyncService sync,
    GoogleCalendarService googleService,
    SemaphoreSlim gate,
    CancellationToken ct) =>
{
    if (!googleService.IsAuthenticated)
        return Results.Json(
            new { error = "Google is not authenticated. Open /auth/google first." },
            statusCode: StatusCodes.Status401Unauthorized);

    if (!await gate.WaitAsync(0, ct))
        return Results.Conflict(new { error = "A sync run is already in progress." });

    var startedUtc = DateTime.UtcNow;

    try
    {
        var summary = await sync.RunAsync(ct);
        var finishedUtc = DateTime.UtcNow;

        return Results.Ok(new SyncRunResponse(
            StartedUtc: startedUtc,
            FinishedUtc: finishedUtc,
            Summary: ToSyncSummaryResponse(summary)));
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "Sync failed", detail: ex.Message);
    }
    finally
    {
        gate.Release();
    }
})
    .WithName("RunSync")
    .WithTags("Sync");

app.MapGet("/events", async (SyncDbContext db, CancellationToken ct, bool includeDeleted = false) =>
{
    var query = db.Events.AsNoTracking();

    if (!includeDeleted)
        query = query.Where(e => !e.IsDeleted);

    var events = await query
        .OrderBy(e => e.StartUtc)
        .Select(e => new EventResponse(
            e.Id,
            e.GoogleEventId,
            e.Summary,
            e.Description,
            e.Location,
            e.StartUtc,
            e.EndUtc,
            e.IsAllDay,
            e.IsDeleted,
            e.IsDirty,
            e.LastModified,
            e.GoogleUpdated))
        .ToListAsync(ct);

    return Results.Ok(new EventListResponse(events.Count, events));
})
    .WithName("ListEvents")
    .WithTags("Events");

app.MapGet("/events/{id:int}", async (int id, SyncDbContext db, CancellationToken ct) =>
{
    var local = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
    return local is null ? Results.NotFound() : Results.Ok(ToEventResponse(local));
})
    .WithName("GetEvent")
    .WithTags("Events");

app.MapPost("/events", async (EventRequest request, SyncDbContext db, CancellationToken ct) =>
{
    var validationError = ValidateEvent(request);
    if (validationError is not null)
        return Results.BadRequest(new { error = validationError });

    var local = new LocalEvent();
    ApplyRequest(local, request);
    local.IsDirty = true;
    local.LastModified = DateTime.UtcNow;

    db.Events.Add(local);
    await db.SaveChangesAsync(ct);

    return Results.Created($"/events/{local.Id}", ToEventResponse(local));
})
    .WithName("CreateEvent")
    .WithTags("Events");

app.MapPut("/events/{id:int}", async (int id, EventRequest request, SyncDbContext db, CancellationToken ct) =>
{
    var validationError = ValidateEvent(request);
    if (validationError is not null)
        return Results.BadRequest(new { error = validationError });

    var local = await db.Events.FirstOrDefaultAsync(e => e.Id == id, ct);
    if (local is null)
        return Results.NotFound();

    ApplyRequest(local, request);
    local.IsDeleted = false;
    local.IsDirty = true;
    local.LastModified = DateTime.UtcNow;

    await db.SaveChangesAsync(ct);

    return Results.Ok(ToEventResponse(local));
})
    .WithName("UpdateEvent")
    .WithTags("Events");

app.MapDelete("/events/{id:int}", async (int id, SyncDbContext db, CancellationToken ct) =>
{
    var local = await db.Events.FirstOrDefaultAsync(e => e.Id == id, ct);
    if (local is null)
        return Results.NotFound();

    if (string.IsNullOrEmpty(local.GoogleEventId))
    {
        db.Events.Remove(local);
    }
    else
    {
        local.IsDeleted = true;
        local.IsDirty = true;
        local.LastModified = DateTime.UtcNow;
    }

    await db.SaveChangesAsync(ct);

    return Results.NoContent();
})
    .WithName("DeleteEvent")
    .WithTags("Events");

await app.RunAsync();

static string ResolveDatabasePath(string configuredPath)
{
    var path = string.IsNullOrWhiteSpace(configuredPath) ? "sync.db" : configuredPath;
    return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
}

static string BuildGoogleRedirectUri(HttpRequest request) =>
    $"{request.Scheme}://{request.Host}/auth/google/callback";

static string? ValidateEvent(EventRequest request)
{
    if (request.EndUtc <= request.StartUtc)
        return "EndUtc must be later than StartUtc.";

    return null;
}

static void ApplyRequest(LocalEvent local, EventRequest request)
{
    local.Summary = request.Summary;
    local.Description = request.Description;
    local.Location = request.Location;
    local.StartUtc = AsUtc(request.StartUtc);
    local.EndUtc = AsUtc(request.EndUtc);
    local.IsAllDay = request.IsAllDay;
}

static DateTime AsUtc(DateTime value) =>
    value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

static EventResponse ToEventResponse(LocalEvent local) =>
    new(
        local.Id,
        local.GoogleEventId,
        local.Summary,
        local.Description,
        local.Location,
        local.StartUtc,
        local.EndUtc,
        local.IsAllDay,
        local.IsDeleted,
        local.IsDirty,
        local.LastModified,
        local.GoogleUpdated);

static SyncSummaryResponse ToSyncSummaryResponse(SyncService.SyncSummary summary) =>
    new(
        summary.PulledCreated,
        summary.PulledUpdated,
        summary.PulledDeleted,
        summary.PushedCreated,
        summary.PushedUpdated,
        summary.PushedDeleted,
        summary.Conflicts);

public sealed record EventRequest(
    string? Summary,
    string? Description,
    string? Location,
    DateTime StartUtc,
    DateTime EndUtc,
    bool IsAllDay);

public sealed record EventResponse(
    int Id,
    string? GoogleEventId,
    string? Summary,
    string? Description,
    string? Location,
    DateTime StartUtc,
    DateTime EndUtc,
    bool IsAllDay,
    bool IsDeleted,
    bool IsDirty,
    DateTime LastModified,
    DateTime? GoogleUpdated);

public sealed record EventListResponse(
    int Count,
    IReadOnlyList<EventResponse> Events);

public sealed record SyncRunResponse(
    DateTime StartedUtc,
    DateTime FinishedUtc,
    SyncSummaryResponse Summary);

public sealed record SyncSummaryResponse(
    int PulledCreated,
    int PulledUpdated,
    int PulledDeleted,
    int PushedCreated,
    int PushedUpdated,
    int PushedDeleted,
    int Conflicts);
