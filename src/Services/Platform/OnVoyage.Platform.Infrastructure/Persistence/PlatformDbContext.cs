using Microsoft.EntityFrameworkCore;

namespace OnVoyage.Platform.Infrastructure.Persistence;

internal sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public const string Schema = "platform";

    public DbSet<RemoteConfigRow> RemoteConfig => Set<RemoteConfigRow>();
    public DbSet<RemoteConfigHistoryRow> RemoteConfigHistory => Set<RemoteConfigHistoryRow>();
    public DbSet<FeatureFlagRow> FeatureFlags => Set<FeatureFlagRow>();
    public DbSet<ConsentRow> Consents => Set<ConsentRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<RemoteConfigRow>(entity =>
        {
            entity.ToTable("remote_config");
            entity.HasKey(row => row.Key);
            entity.Property(row => row.Value).HasColumnType("jsonb");
        });

        // Append-only: every published version stays readable (history screen, T-408; audit, T-409).
        modelBuilder.Entity<RemoteConfigHistoryRow>(entity =>
        {
            entity.ToTable("remote_config_history");
            entity.HasKey(row => new { row.Key, row.Version });
            entity.Property(row => row.Value).HasColumnType("jsonb");
        });

        modelBuilder.Entity<FeatureFlagRow>(entity =>
        {
            entity.ToTable("feature_flag", table => table.HasCheckConstraint("ck_feature_flag_rollout", "rollout_percent between 0 and 100"));
            entity.HasKey(row => row.Name);
        });

        modelBuilder.Entity<ConsentRow>(entity =>
        {
            entity.ToTable("consent", table => table.HasCheckConstraint("ck_consent_kind", "kind in ('analytics', 'ads_personalization')"));
            entity.HasKey(row => new { row.TravelerId, row.Kind });
        });

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
