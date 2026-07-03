using Microsoft.EntityFrameworkCore;

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
    }

    // --- Metadata helpers --------------------------------------------------

    public async Task<string?> GetMetadataAsync(string key, CancellationToken ct = default)
    {
        var row = await Metadata.FindAsync(new object[] { key }, ct);
        return row?.Value;
    }

    public async Task SetMetadataAsync(string key, string? value, CancellationToken ct = default)
    {
        var row = await Metadata.FindAsync(new object[] { key }, ct);
        if (row is null)
        {
            Metadata.Add(new SyncMetadata { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
    }
}
