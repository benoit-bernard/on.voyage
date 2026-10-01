using Microsoft.EntityFrameworkCore;

namespace OnVoyage.Insights.Infrastructure.Persistence;

internal sealed class InsightsDbContext(DbContextOptions<InsightsDbContext> options) : DbContext(options)
{
    public const string Schema = "insights";

    public DbSet<EventRow> Events => Set<EventRow>();
    public DbSet<ConsentProjectionRow> Consents => Set<ConsentProjectionRow>();
    public DbSet<DailyKpiRow> DailyKpis => Set<DailyKpiRow>();
    public DbSet<ConfigSnapshotRow> ConfigSnapshot => Set<ConfigSnapshotRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<EventRow>(e =>
        {
            // The migration creates this table by hand: EF Core cannot declare PARTITION BY RANGE (occurred_at).
            e.ToTable("event");
            e.HasKey(r => new { r.Id, r.OccurredAt });
            e.Property(r => r.Props).HasColumnType("jsonb");
            e.HasIndex(r => new { r.TravelerRef, r.OccurredAt });
            e.HasIndex(r => new { r.Name, r.OccurredAt });
        });

        modelBuilder.Entity<ConsentProjectionRow>(e =>
        {
            e.ToTable("consent_projection");
            e.HasKey(r => r.TravelerId);
        });

        modelBuilder.Entity<DailyKpiRow>(e =>
        {
            e.ToTable("daily_kpi");
            e.HasKey(r => new { r.Date, r.DestinationId, r.Cohort, r.Metric });
        });

        modelBuilder.Entity<ConfigSnapshotRow>(e =>
        {
            e.ToTable("config_snapshot");
            e.HasKey(r => r.Key);
            e.Property(r => r.Value).HasColumnType("jsonb");
        });

        ApplySnakeCaseColumns(modelBuilder);
    }

    private static void ApplySnakeCaseColumns(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnake(property.GetColumnName()));
            }
        }
    }

    private static string ToSnake(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
