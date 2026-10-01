using Microsoft.EntityFrameworkCore;

namespace OnVoyage.Platform.Infrastructure.Persistence;

internal sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public const string Schema = "platform";

    public DbSet<RemoteConfigRow> RemoteConfig => Set<RemoteConfigRow>();
    public DbSet<RemoteConfigHistoryRow> RemoteConfigHistory => Set<RemoteConfigHistoryRow>();
    public DbSet<FeatureFlagRow> FeatureFlags => Set<FeatureFlagRow>();
    public DbSet<ConsentRow> Consents => Set<ConsentRow>();
    public DbSet<AccountRow> Accounts => Set<AccountRow>();
    public DbSet<OtpChallengeRow> OtpChallenges => Set<OtpChallengeRow>();
    public DbSet<RefreshTokenRow> RefreshTokens => Set<RefreshTokenRow>();
    public DbSet<AdminAuditRow> AdminAudit => Set<AdminAuditRow>();
    public DbSet<DeletionRequestRow> DeletionRequests => Set<DeletionRequestRow>();
    public DbSet<DeletionAckRow> DeletionAcks => Set<DeletionAckRow>();
    public DbSet<DeletionLogRow> DeletionLog => Set<DeletionLogRow>();
    public DbSet<ExportRequestRow> ExportRequests => Set<ExportRequestRow>();
    public DbSet<ExportPartRow> ExportParts => Set<ExportPartRow>();

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

        // Append-only journal of admin writes from every service (SEC-10). The event id makes redelivery harmless.
        modelBuilder.Entity<AdminAuditRow>(entity =>
        {
            entity.ToTable("admin_audit");
            entity.HasKey(row => row.EventId);
            entity.HasIndex(row => row.At);
            entity.HasIndex(row => new { row.Service, row.At });
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

        modelBuilder.Entity<AccountRow>(entity =>
        {
            entity.ToTable("account");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.Email).IsUnique().HasFilter("email is not null");
            entity.HasIndex(row => row.LastActiveAt);
        });

        // Only a keyed hash of the code is stored (never the code itself).
        modelBuilder.Entity<OtpChallengeRow>(entity =>
        {
            entity.ToTable("otp_challenge");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.Email, row.CreatedAt });
        });

        modelBuilder.Entity<RefreshTokenRow>(entity =>
        {
            entity.ToTable("refresh_token");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.TokenHash).IsUnique();
            entity.HasIndex(row => row.FamilyId);
            entity.HasIndex(row => row.AccountId);
        });

        modelBuilder.Entity<DeletionRequestRow>(entity =>
        {
            entity.ToTable("deletion_request");
            entity.HasKey(row => row.TravelerId);
        });

        modelBuilder.Entity<DeletionAckRow>(entity =>
        {
            entity.ToTable("deletion_ack");
            entity.HasKey(row => new { row.TravelerId, row.Service });
        });

        modelBuilder.Entity<DeletionLogRow>(entity =>
        {
            entity.ToTable("deletion_log");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedOnAdd();
        });

        modelBuilder.Entity<ExportRequestRow>(entity =>
        {
            entity.ToTable("export_request");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.TravelerId);
            entity.HasIndex(row => row.ExpiresAt);
        });

        modelBuilder.Entity<ExportPartRow>(entity =>
        {
            entity.ToTable("export_part");
            entity.HasKey(row => new { row.ExportId, row.Service });
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
