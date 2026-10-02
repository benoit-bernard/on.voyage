using Microsoft.EntityFrameworkCore;

namespace OnVoyage.Creators.Infrastructure.Persistence;

internal sealed class CreatorsDbContext(DbContextOptions<CreatorsDbContext> options) : DbContext(options)
{
    public const string Schema = "creators";

    // Names of the unique indexes, read back from the database error to tell which rule refused a write.
    public const string HandleIndex = "ux_creator_handle";
    public const string AccountIndex = "ux_creator_account";
    public const string ContentIndex = "ux_content_item_platform_external_id";
    public const string ConnectedAccountIndex = "ux_connected_account_platform_external_user";

    public DbSet<CreatorRow> Creators => Set<CreatorRow>();
    public DbSet<ContentRow> Contents => Set<ContentRow>();
    public DbSet<PlaceLinkRow> PlaceLinks => Set<PlaceLinkRow>();
    public DbSet<TipRow> Tips => Set<TipRow>();
    public DbSet<FollowRow> Follows => Set<FollowRow>();
    public DbSet<PoiDirectoryRow> Pois => Set<PoiDirectoryRow>();
    public DbSet<ModerationCaseRow> Cases => Set<ModerationCaseRow>();
    public DbSet<ConnectedAccountRow> ConnectedAccounts => Set<ConnectedAccountRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.HasPostgresExtension("citext");
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<CreatorRow>(e =>
        {
            e.ToTable("creator", t => t.HasCheckConstraint("ck_creator_status", "status in ('draft', 'published', 'suspended')"));
            e.HasKey(r => r.Id);
            e.Property(r => r.Handle).HasColumnType("citext").HasMaxLength(30);
            e.HasIndex(r => r.Handle).IsUnique().HasDatabaseName(HandleIndex);
            e.HasIndex(r => r.AccountId).IsUnique().HasFilter("account_id is not null").HasDatabaseName(AccountIndex);
            e.HasIndex(r => r.Status);
            e.Property(r => r.Links).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ContentRow>(e =>
        {
            e.ToTable("content_item", t =>
            {
                t.HasCheckConstraint("ck_content_item_platform", "platform in ('instagram', 'youtube', 'tiktok')");
                t.HasCheckConstraint("ck_content_item_kind", "kind in ('video', 'photo', 'carousel', 'article')");
                t.HasCheckConstraint("ck_content_item_status", "status in ('imported', 'hidden', 'removed')");
                t.HasCheckConstraint("ck_content_item_excerpt", "char_length(caption_excerpt) <= 500");
            });
            e.HasKey(r => r.Id);
            e.HasOne<CreatorRow>().WithMany().HasForeignKey(r => r.CreatorId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.Platform, r.ExternalId }).IsUnique().HasDatabaseName(ContentIndex);
            e.HasIndex(r => r.CreatorId);
            e.HasIndex(r => r.ConnectedAccountId);
            e.HasOne<ConnectedAccountRow>().WithMany().HasForeignKey(r => r.ConnectedAccountId).OnDelete(DeleteBehavior.SetNull);
            e.Property(r => r.Chapters).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ConnectedAccountRow>(e =>
        {
            e.ToTable("connected_account", t =>
            {
                t.HasCheckConstraint("ck_connected_account_platform", "platform in ('instagram', 'youtube', 'tiktok')");
                t.HasCheckConstraint("ck_connected_account_status", "status in ('active', 'needs_reauth')");
            });
            e.HasKey(r => r.Id);
            e.HasOne<CreatorRow>().WithMany().HasForeignKey(r => r.CreatorId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.CreatorId, r.Platform }).IsUnique().HasDatabaseName("ux_connected_account_creator_platform");
            e.HasIndex(r => new { r.Platform, r.ExternalUserId }).IsUnique().HasDatabaseName(ConnectedAccountIndex);
            e.HasIndex(r => r.LastSyncAt);
        });

        modelBuilder.Entity<PlaceLinkRow>(e =>
        {
            e.ToTable("place_link", t => t.HasCheckConstraint("ck_place_link_status", "status in ('proposed', 'validated', 'rejected')"));
            e.HasKey(r => r.Id);
            e.HasOne<CreatorRow>().WithMany().HasForeignKey(r => r.CreatorId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ContentRow>().WithMany().HasForeignKey(r => r.ContentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.CreatorId, r.PoiId, r.ContentId, r.StartS }).IsUnique().AreNullsDistinct(false);
            e.HasIndex(r => new { r.PoiId, r.Status });
            e.HasIndex(r => r.ContentId);
            e.Property(r => r.Signals).HasColumnType("jsonb");
        });

        modelBuilder.Entity<TipRow>(e =>
        {
            e.ToTable("creator_tip", t =>
            {
                t.HasCheckConstraint("ck_creator_tip_text", "char_length(\"text\") <= 280");
                t.HasCheckConstraint("ck_creator_tip_status", "status in ('published', 'hidden')");
            });
            e.HasKey(r => new { r.CreatorId, r.PoiId });
            e.HasOne<CreatorRow>().WithMany().HasForeignKey(r => r.CreatorId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => r.Id).IsUnique(); // a tip can be the target of a report
        });

        modelBuilder.Entity<FollowRow>(e =>
        {
            e.ToTable("follow");
            e.HasKey(r => new { r.TravelerId, r.CreatorId });
            e.HasOne<CreatorRow>().WithMany().HasForeignKey(r => r.CreatorId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => r.CreatorId);
        });

        modelBuilder.Entity<PoiDirectoryRow>(e =>
        {
            e.ToTable("poi_directory");
            e.HasKey(r => r.PoiId);
            e.Property(r => r.Names).HasColumnType("jsonb");
            e.HasIndex(r => r.SearchText).HasMethod("gin").HasOperators("gin_trgm_ops");
            e.HasIndex(r => r.DestinationSlug);
        });

        modelBuilder.Entity<ModerationCaseRow>(e =>
        {
            e.ToTable("moderation_case", t =>
            {
                t.HasCheckConstraint("ck_moderation_case_target", "target_type in ('creator', 'content', 'place_link', 'tip')");
                t.HasCheckConstraint("ck_moderation_case_status", "status in ('open', 'decided')");
            });
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.Status, r.CreatedAt });
            e.HasIndex(r => r.ReporterRef);
            e.HasIndex(r => new { r.TargetType, r.TargetId });
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
