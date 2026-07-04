using GoogleCalendarSync.Configuration;
using GoogleCalendarSync.Data;
using GoogleCalendarSync.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using System.Collections.Concurrent;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);

var googleOptions = builder.Configuration.GetSection("Google").Get<GoogleOptions>() ?? new GoogleOptions();
var syncOptions = builder.Configuration.GetSection("Sync").Get<SyncOptions>() ?? new SyncOptions();
syncOptions.DatabasePath = ResolveDatabasePath(syncOptions.DatabasePath);

var syncGates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

builder.Services.AddSingleton(googleOptions);
builder.Services.AddSingleton(syncOptions);
builder.Services.AddSingleton(syncGates);
builder.Services.AddSingleton(sp => new GoogleCalendarService(sp.GetRequiredService<GoogleOptions>()));
builder.Services.AddScoped(sp => new SyncDbContext(sp.GetRequiredService<SyncOptions>().DatabasePath));
builder.Services.AddScoped<SyncService>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/auth/google";
        options.AccessDeniedPath = "/auth/status";
    });
builder.Services.AddAuthorization();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "GoogleCalendarSync API",
        Version = "v1",
        Description = "Multi-employee Web API for Google Calendar OAuth, two-way sync, and local event management."
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "GoogleCalendarSync API v1");
    options.RoutePrefix = "swagger";
});
app.UseAuthentication();
app.UseAuthorization();

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
    await db.EnsureAppSchemaAsync();
}

Console.WriteLine($"SQLite database: {syncOptions.DatabasePath}");
Console.WriteLine("Employees authenticate individually at /auth/google.");

app.MapGet("/", () => Results.Ok(new
{
    name = "GoogleCalendarSync API",
    mode = "Multi-employee OAuth",
    endpoints = new[]
    {
        "GET /health",
        "GET /auth/status",
        "GET /auth/google?loginHint=employee@example.com",
        "GET /auth/google/callback",
        "POST /auth/logout",
        "GET /employees",
        "GET /employees/{employeeEmail}/auth/status",
        "GET /employees/{employeeEmail}/sync/status",
        "POST /employees/{employeeEmail}/sync/run",
        "GET /employees/{employeeEmail}/events",
        "GET /employees/{employeeEmail}/events/{id}",
        "POST /employees/{employeeEmail}/events",
        "PUT /employees/{employeeEmail}/events/{id}",
        "DELETE /employees/{employeeEmail}/events/{id}",
        "GET /swagger",
        "GET /swagger/v1/swagger.json"
    }
}))
    .WithName("GetApiIndex")
    .WithTags("System");

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("GetHealth")
    .WithTags("System");

app.MapGet("/auth/status", async (HttpContext context, GoogleCalendarService googleService, CancellationToken ct) =>
{
    var employees = await googleService.ListAuthenticatedEmployeesAsync(ct);
    return Results.Ok(new AuthStatusResponse(
        GetCurrentEmployeeEmail(context),
        employees.Count,
        employees));
})
    .WithName("GetAuthStatus")
    .WithTags("Authentication");

app.MapGet("/auth/google", (string? loginHint, HttpRequest request, GoogleCalendarService googleService) =>
{
    var redirectUri = BuildGoogleRedirectUri(request);
    var authorizationUrl = googleService.CreateAuthorizationUrl(redirectUri, loginHint);
    return Results.Redirect(authorizationUrl);
})
    .WithName("StartGoogleAuth")
    .WithTags("Authentication");

app.MapGet("/auth/google/callback", async (
    string? code,
    string? error,
    HttpContext context,
    HttpRequest request,
    GoogleCalendarService googleService,
    CancellationToken ct) =>
{
    if (!string.IsNullOrWhiteSpace(error))
        return Results.BadRequest(new { error });

    if (string.IsNullOrWhiteSpace(code))
        return Results.BadRequest(new { error = "Missing authorization code." });

    var employee = await googleService.AuthenticateWithCodeAsync(code, BuildGoogleRedirectUri(request), ct);
    await SignInEmployeeAsync(context, employee);

    return Results.Ok(new AuthenticatedEmployeeResponse(
        employee.Email,
        employee.Name,
        employee.EmailVerified,
        employee.HostedDomain,
        $"/employees/{Uri.EscapeDataString(employee.Email)}/sync/run",
        $"/employees/{Uri.EscapeDataString(employee.Email)}/events"));
})
    .WithName("CompleteGoogleAuth")
    .WithTags("Authentication");

app.MapPost("/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { signedOut = true });
})
    .WithName("Logout")
    .WithTags("Authentication");

app.MapGet("/employees", async (HttpContext context, GoogleCalendarService googleService, CancellationToken ct) =>
{
    var employees = await googleService.ListAuthenticatedEmployeesAsync(ct);
    return Results.Ok(new AuthStatusResponse(
        GetCurrentEmployeeEmail(context),
        employees.Count,
        employees));
})
    .WithName("ListEmployees")
    .WithTags("Employees");

app.MapGet("/employees/{employeeEmail}/auth/status", async (
    string employeeEmail,
    HttpContext context,
    GoogleCalendarService googleService,
    CancellationToken ct) =>
{
    if (NormalizeEmployeeEmail(employeeEmail) is not { } normalizedEmail)
        return Results.BadRequest(new { error = "employeeEmail must be a valid email address." });

    if (ValidateEmployeeAccess(context, normalizedEmail) is { } accessError)
        return accessError;

    var isAuthenticated = await googleService.IsAuthenticatedAsync(normalizedEmail, ct);
    return Results.Ok(new EmployeeAuthStatusResponse(normalizedEmail, isAuthenticated));
})
    .WithName("GetEmployeeAuthStatus")
    .WithTags("Employees");

app.MapGet("/employees/{employeeEmail}/sync/status", async (
    string employeeEmail,
    HttpContext context,
    SyncDbContext db,
    SyncOptions options,
    GoogleCalendarService googleService,
    ConcurrentDictionary<string, SemaphoreSlim> gates,
    CancellationToken ct,
    string calendarId = GoogleCalendarService.DefaultCalendarId) =>
{
    if (NormalizeEmployeeEmail(employeeEmail) is not { } normalizedEmail)
        return Results.BadRequest(new { error = "employeeEmail must be a valid email address." });

    if (ValidateEmployeeAccess(context, normalizedEmail) is { } accessError)
        return accessError;

    calendarId = NormalizeCalendarId(calendarId);
    var lastSyncUtc = await db.GetMetadataAsync(normalizedEmail, calendarId, SyncMetadata.Keys.LastSyncUtc, ct);
    var hasSyncToken = !string.IsNullOrEmpty(await db.GetMetadataAsync(
        normalizedEmail,
        calendarId,
        SyncMetadata.Keys.NextSyncToken,
        ct));
    var isAuthenticated = await googleService.IsAuthenticatedAsync(normalizedEmail, ct);
    var gate = GetSyncGate(gates, normalizedEmail, calendarId);

    return Results.Ok(new SyncStatusResponse(
        normalizedEmail,
        calendarId,
        gate.CurrentCount == 0,
        isAuthenticated,
        lastSyncUtc,
        hasSyncToken,
        options.IntervalMinutes,
        options.ConflictStrategy.ToString(),
        options.DatabasePath));
})
    .WithName("GetEmployeeSyncStatus")
    .WithTags("Sync");

app.MapPost("/employees/{employeeEmail}/sync/run", async (
    string employeeEmail,
    HttpContext context,
    SyncService sync,
    GoogleCalendarService googleService,
    ConcurrentDictionary<string, SemaphoreSlim> gates,
    CancellationToken ct,
    string calendarId = GoogleCalendarService.DefaultCalendarId) =>
{
    if (NormalizeEmployeeEmail(employeeEmail) is not { } normalizedEmail)
        return Results.BadRequest(new { error = "employeeEmail must be a valid email address." });

    if (ValidateEmployeeAccess(context, normalizedEmail) is { } accessError)
        return accessError;

    calendarId = NormalizeCalendarId(calendarId);

    if (!await googleService.IsAuthenticatedAsync(normalizedEmail, ct))
    {
        return Results.Json(
            new
            {
                error = $"Google is not authenticated for '{normalizedEmail}'.",
                authUrl = $"/auth/google?loginHint={Uri.EscapeDataString(normalizedEmail)}"
            },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var gate = GetSyncGate(gates, normalizedEmail, calendarId);
    if (!await gate.WaitAsync(0, ct))
        return Results.Conflict(new { error = "A sync run is already in progress for this employee/calendar." });

    var startedUtc = DateTime.UtcNow;

    try
    {
        var summary = await sync.RunAsync(normalizedEmail, calendarId, ct);
        var finishedUtc = DateTime.UtcNow;

        return Results.Ok(new SyncRunResponse(
            normalizedEmail,
            calendarId,
            startedUtc,
            finishedUtc,
            ToSyncSummaryResponse(summary)));
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
    .WithName("RunEmployeeSync")
    .WithTags("Sync");

app.MapGet("/employees/{employeeEmail}/events", async (
    string employeeEmail,
    HttpContext context,
    SyncDbContext db,
    CancellationToken ct,
    string calendarId = GoogleCalendarService.DefaultCalendarId,
    bool includeDeleted = false) =>
{
    if (NormalizeEmployeeEmail(employeeEmail) is not { } normalizedEmail)
        return Results.BadRequest(new { error = "employeeEmail must be a valid email address." });

    if (ValidateEmployeeAccess(context, normalizedEmail) is { } accessError)
        return accessError;

    calendarId = NormalizeCalendarId(calendarId);
    var query = db.Events
        .AsNoTracking()
        .Where(e => e.OwnerEmail == normalizedEmail && e.CalendarId == calendarId);

    if (!includeDeleted)
        query = query.Where(e => !e.IsDeleted);

    var events = await query
        .OrderBy(e => e.StartUtc)
        .Select(e => new EventResponse(
            e.Id,
            e.OwnerEmail,
            e.CalendarId,
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

    return Results.Ok(new EventListResponse(normalizedEmail, calendarId, events.Count, events));
})
    .WithName("ListEmployeeEvents")
    .WithTags("Events");

app.MapGet("/employees/{employeeEmail}/events/{id:int}", async (
    string employeeEmail,
    int id,
    HttpContext context,
    SyncDbContext db,
    CancellationToken ct,
    string calendarId = GoogleCalendarService.DefaultCalendarId) =>
{
    if (NormalizeEmployeeEmail(employeeEmail) is not { } normalizedEmail)
        return Results.BadRequest(new { error = "employeeEmail must be a valid email address." });

    if (ValidateEmployeeAccess(context, normalizedEmail) is { } accessError)
        return accessError;

    calendarId = NormalizeCalendarId(calendarId);
    var local = await db.Events
        .AsNoTracking()
        .FirstOrDefaultAsync(e =>
            e.Id == id &&
            e.OwnerEmail == normalizedEmail &&
            e.CalendarId == calendarId, ct);

    return local is null ? Results.NotFound() : Results.Ok(ToEventResponse(local));
})
    .WithName("GetEmployeeEvent")
    .WithTags("Events");

app.MapPost("/employees/{employeeEmail}/events", async (
    string employeeEmail,
    HttpContext context,
    EventRequest request,
    SyncDbContext db,
    CancellationToken ct,
    string calendarId = GoogleCalendarService.DefaultCalendarId) =>
{
    if (NormalizeEmployeeEmail(employeeEmail) is not { } normalizedEmail)
        return Results.BadRequest(new { error = "employeeEmail must be a valid email address." });

    if (ValidateEmployeeAccess(context, normalizedEmail) is { } accessError)
        return accessError;

    var validationError = ValidateEvent(request);
    if (validationError is not null)
        return Results.BadRequest(new { error = validationError });

    calendarId = NormalizeCalendarId(calendarId);
    var local = new LocalEvent
    {
        OwnerEmail = normalizedEmail,
        CalendarId = calendarId,
        IsDirty = true,
        LastModified = DateTime.UtcNow
    };
    ApplyRequest(local, request);

    db.Events.Add(local);
    await db.SaveChangesAsync(ct);

    return Results.Created(
        $"/employees/{Uri.EscapeDataString(normalizedEmail)}/events/{local.Id}",
        ToEventResponse(local));
})
    .WithName("CreateEmployeeEvent")
    .WithTags("Events");

app.MapPut("/employees/{employeeEmail}/events/{id:int}", async (
    string employeeEmail,
    int id,
    HttpContext context,
    EventRequest request,
    SyncDbContext db,
    CancellationToken ct,
    string calendarId = GoogleCalendarService.DefaultCalendarId) =>
{
    if (NormalizeEmployeeEmail(employeeEmail) is not { } normalizedEmail)
        return Results.BadRequest(new { error = "employeeEmail must be a valid email address." });

    if (ValidateEmployeeAccess(context, normalizedEmail) is { } accessError)
        return accessError;

    var validationError = ValidateEvent(request);
    if (validationError is not null)
        return Results.BadRequest(new { error = validationError });

    calendarId = NormalizeCalendarId(calendarId);
    var local = await db.Events.FirstOrDefaultAsync(e =>
        e.Id == id &&
        e.OwnerEmail == normalizedEmail &&
        e.CalendarId == calendarId, ct);

    if (local is null)
        return Results.NotFound();

    ApplyRequest(local, request);
    local.IsDeleted = false;
    local.IsDirty = true;
    local.LastModified = DateTime.UtcNow;

    await db.SaveChangesAsync(ct);

    return Results.Ok(ToEventResponse(local));
})
    .WithName("UpdateEmployeeEvent")
    .WithTags("Events");

app.MapDelete("/employees/{employeeEmail}/events/{id:int}", async (
    string employeeEmail,
    int id,
    HttpContext context,
    SyncDbContext db,
    CancellationToken ct,
    string calendarId = GoogleCalendarService.DefaultCalendarId) =>
{
    if (NormalizeEmployeeEmail(employeeEmail) is not { } normalizedEmail)
        return Results.BadRequest(new { error = "employeeEmail must be a valid email address." });

    if (ValidateEmployeeAccess(context, normalizedEmail) is { } accessError)
        return accessError;

    calendarId = NormalizeCalendarId(calendarId);
    var local = await db.Events.FirstOrDefaultAsync(e =>
        e.Id == id &&
        e.OwnerEmail == normalizedEmail &&
        e.CalendarId == calendarId, ct);

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
    .WithName("DeleteEmployeeEvent")
    .WithTags("Events");

await app.RunAsync();

static string ResolveDatabasePath(string configuredPath)
{
    var path = string.IsNullOrWhiteSpace(configuredPath) ? "sync.db" : configuredPath;
    return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
}

static string BuildGoogleRedirectUri(HttpRequest request) =>
    $"{request.Scheme}://{request.Host}/auth/google/callback";

static async Task SignInEmployeeAsync(
    HttpContext context,
    GoogleCalendarService.AuthenticatedEmployee employee)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, employee.Email),
        new(ClaimTypes.Email, employee.Email)
    };

    if (!string.IsNullOrWhiteSpace(employee.Name))
        claims.Add(new Claim(ClaimTypes.Name, employee.Name));

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
        });
}

static string? GetCurrentEmployeeEmail(HttpContext context)
{
    if (context.User.Identity?.IsAuthenticated != true)
        return null;

    var email =
        context.User.FindFirstValue(ClaimTypes.Email) ??
        context.User.FindFirstValue(ClaimTypes.NameIdentifier);

    return string.IsNullOrWhiteSpace(email) ? null : NormalizeEmployeeEmail(email);
}

static IResult? ValidateEmployeeAccess(HttpContext context, string employeeEmail)
{
    var currentEmployeeEmail = GetCurrentEmployeeEmail(context);

    if (currentEmployeeEmail is null)
    {
        return Results.Json(
            new
            {
                error = "Sign in with Google before using employee routes.",
                authUrl = $"/auth/google?loginHint={Uri.EscapeDataString(employeeEmail)}"
            },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    if (!string.Equals(currentEmployeeEmail, employeeEmail, StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(
            new
            {
                error = $"Signed in as '{currentEmployeeEmail}', not '{employeeEmail}'."
            },
            statusCode: StatusCodes.Status403Forbidden);
    }

    return null;
}

static string? NormalizeEmployeeEmail(string employeeEmail)
{
    var normalized = employeeEmail.Trim().ToLowerInvariant();
    return normalized.Contains('@', StringComparison.Ordinal) ? normalized : null;
}

static string NormalizeCalendarId(string calendarId) =>
    string.IsNullOrWhiteSpace(calendarId) ? GoogleCalendarService.DefaultCalendarId : calendarId.Trim();

static SemaphoreSlim GetSyncGate(
    ConcurrentDictionary<string, SemaphoreSlim> gates,
    string employeeEmail,
    string calendarId) =>
    gates.GetOrAdd($"{employeeEmail}|{calendarId}", _ => new SemaphoreSlim(1, 1));

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
        local.OwnerEmail,
        local.CalendarId,
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
    string OwnerEmail,
    string CalendarId,
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
    string EmployeeEmail,
    string CalendarId,
    int Count,
    IReadOnlyList<EventResponse> Events);

public sealed record AuthStatusResponse(
    string? CurrentEmployeeEmail,
    int Count,
    IReadOnlyList<string> Employees);

public sealed record EmployeeAuthStatusResponse(
    string EmployeeEmail,
    bool IsAuthenticated);

public sealed record AuthenticatedEmployeeResponse(
    string Email,
    string? Name,
    bool EmailVerified,
    string? HostedDomain,
    string SyncUrl,
    string EventsUrl);

public sealed record SyncStatusResponse(
    string EmployeeEmail,
    string CalendarId,
    bool IsRunning,
    bool IsAuthenticated,
    string? LastSyncUtc,
    bool HasSyncToken,
    int IntervalMinutes,
    string ConflictStrategy,
    string DatabasePath);

public sealed record SyncRunResponse(
    string EmployeeEmail,
    string CalendarId,
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
