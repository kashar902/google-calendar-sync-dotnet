using Microsoft.EntityFrameworkCore;
using System.Data;

namespace GoogleCalendarSync.Data;

public class SyncDbContext : DbContext
{
    private readonly string _databasePath;

    public SyncDbContext(string databasePath)
    {
        _databasePath = databasePath;
    }

    public DbSet<LocalEvent> Events => Set<LocalEvent>();
    public DbSet<SyncMetadata> Metadata => Set<SyncMetadata>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite($"Data Source={_databasePath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LocalEvent>()
            .HasIndex(e => e.GoogleEventId);

        modelBuilder.Entity<LocalEvent>()
            .HasIndex(e => new { e.OwnerEmail, e.CalendarId, e.GoogleEventId });
    }

    // --- Metadata helpers --------------------------------------------------

    public async Task EnsureAppSchemaAsync(CancellationToken ct = default)
    {
        await EnsureColumnAsync("Events", "OwnerEmail", "TEXT NOT NULL DEFAULT ''", ct);
        await EnsureColumnAsync("Events", "CalendarId", "TEXT NOT NULL DEFAULT 'primary'", ct);
    }

    public Task<string?> GetMetadataAsync(string key, CancellationToken ct = default) =>
        GetMetadataAsync(null, null, key, ct);

    public async Task<string?> GetMetadataAsync(
        string? ownerEmail,
        string? calendarId,
        string key,
        CancellationToken ct = default)
    {
        var row = await Metadata.FindAsync(new object[] { BuildMetadataKey(ownerEmail, calendarId, key) }, ct);
        return row?.Value;
    }

    public Task SetMetadataAsync(string key, string? value, CancellationToken ct = default) =>
        SetMetadataAsync(null, null, key, value, ct);

    public async Task SetMetadataAsync(
        string? ownerEmail,
        string? calendarId,
        string key,
        string? value,
        CancellationToken ct = default)
    {
        var scopedKey = BuildMetadataKey(ownerEmail, calendarId, key);
        var row = await Metadata.FindAsync(new object[] { scopedKey }, ct);
        if (row is null)
        {
            Metadata.Add(new SyncMetadata { Key = scopedKey, Value = value });
        }
        else
        {
            row.Value = value;
        }
    }

    private async Task EnsureColumnAsync(
        string table,
        string column,
        string definition,
        CancellationToken ct)
    {
        var connection = Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({table})";
            await using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                if (string.Equals(reader["name"]?.ToString(), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static string BuildMetadataKey(string? ownerEmail, string? calendarId, string key)
    {
        if (string.IsNullOrWhiteSpace(ownerEmail))
            return key;

        var normalizedEmail = ownerEmail.Trim().ToLowerInvariant();
        var normalizedCalendar = string.IsNullOrWhiteSpace(calendarId) ? "primary" : calendarId.Trim();
        return $"{normalizedEmail}|{normalizedCalendar}|{key}";
    }
}
