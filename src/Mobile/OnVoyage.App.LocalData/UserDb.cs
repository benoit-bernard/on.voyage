using Microsoft.EntityFrameworkCore;

namespace OnVoyage.App.LocalData;

/// <summary>
/// <c>user.db</c> (§14.2). Session, local vector, locks and other small values live in <see cref="Setting"/> as JSON under a key, so a new
/// preference never needs a migration. Nothing here holds a coordinate: a visit is a place and a duration (D-14).
/// </summary>
public sealed class UserDbContext(DbContextOptions<UserDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => Set<Setting>();

    public DbSet<TellEntry> Told => Set<TellEntry>();

    public DbSet<VisitEntry> Visits => Set<VisitEntry>();

    public DbSet<SavedWish> Wishes => Set<SavedWish>();

    public DbSet<CachedResponse> Responses => Set<CachedResponse>();

    public DbSet<SyncItem> SyncItems => Set<SyncItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Setting>(e => e.HasKey(x => x.Key));
        modelBuilder.Entity<TellEntry>(e => e.HasKey(x => x.PoiId));
        modelBuilder.Entity<SavedWish>(e => e.HasKey(x => x.PoiId));
        modelBuilder.Entity<CachedResponse>(e => e.HasKey(x => x.Key));
        modelBuilder.Entity<VisitEntry>(e => e.HasKey(x => x.Id));
        modelBuilder.Entity<SyncItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ClientEventId).IsUnique();
            e.HasIndex(x => x.NextAttemptAt);
        });

        // SQLite has no native DateTimeOffset ordering: store ticks (UTC) so comparisons and ORDER BY are exact.
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()).Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
        {
            property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
        }
    }
}

public sealed class Setting
{
    public string Key { get; set; } = "";

    public string Value { get; set; } = "";
}

/// <summary>When a place was told. Never synchronised: it would reveal a passage nearby (§7, <c>last_reminded_at</c> rule).</summary>
public sealed class TellEntry
{
    public Guid PoiId { get; set; }

    public DateTimeOffset At { get; set; }
}

public sealed class VisitEntry
{
    public long Id { get; set; }

    public Guid PoiId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public int DwellSeconds { get; set; }

    public double Confidence { get; set; }
}

public sealed class SavedWish
{
    public Guid PoiId { get; set; }

    public DateTimeOffset SavedAt { get; set; }
}

public sealed class CachedResponse
{
    public string Key { get; set; } = "";

    public string Body { get; set; } = "";

    public DateTimeOffset FetchedAt { get; set; }
}

/// <summary>An interaction, visit, event or report waiting to be sent. <see cref="ClientEventId"/> makes the send idempotent on the server.</summary>
public sealed class SyncItem
{
    public long Id { get; set; }

    public Guid ClientEventId { get; set; }

    public string Type { get; set; } = "";

    public string Payload { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }
}
