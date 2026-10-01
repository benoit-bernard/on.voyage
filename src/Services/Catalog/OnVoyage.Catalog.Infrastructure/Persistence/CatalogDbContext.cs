using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace OnVoyage.Catalog.Infrastructure.Persistence;

internal sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public const string Schema = "catalog";

    public DbSet<DestinationRow> Destinations => Set<DestinationRow>();
    public DbSet<TaxonomyNodeRow> TaxonomyNodes => Set<TaxonomyNodeRow>();
    public DbSet<PoiRow> Pois => Set<PoiRow>();
    public DbSet<PoiTextRow> PoiTexts => Set<PoiTextRow>();
    public DbSet<PoiInterestRow> PoiInterests => Set<PoiInterestRow>();
    public DbSet<PoiEthicsRow> PoiEthics => Set<PoiEthicsRow>();
    public DbSet<StoryRow> Stories => Set<StoryRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.HasPostgresExtension("pg_trgm");
        modelBuilder.HasPostgresExtension("unaccent");

        modelBuilder.Entity<DestinationRow>(entity =>
        {
            entity.ToTable("destination");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.Slug).IsUnique();
            entity.Property(row => row.Center).HasColumnType("geography (point, 4326)");
            entity.Property(row => row.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<TaxonomyNodeRow>(entity =>
        {
            entity.ToTable("taxonomy_node");
            entity.HasKey(row => row.Code);
        });

        modelBuilder.Entity<PoiRow>(entity =>
        {
            entity.ToTable("poi", table => table.HasCheckConstraint("ck_poi_importance", "importance_score between 0 and 100"));
            entity.HasKey(row => row.Id);
            entity.HasOne(row => row.Destination).WithMany(row => row.Pois).HasForeignKey(row => row.DestinationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(row => new { row.DestinationId, row.Slug }).IsUnique();
            entity.HasIndex(row => row.Location).HasMethod("gist");
            entity.Property(row => row.Location).HasColumnType("geography (point, 4326)");
            entity.Property(row => row.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<PoiTextRow>(entity =>
        {
            entity.ToTable("poi_text");
            entity.HasKey(row => new { row.PoiId, row.Lang });
            entity.HasOne<PoiRow>().WithMany(row => row.Texts).HasForeignKey(row => row.PoiId).OnDelete(DeleteBehavior.Cascade);
            entity.HasGeneratedTsVectorColumn(row => row.SearchVector, "french", row => new { row.Name, row.ShortDescription }).HasIndex(row => row.SearchVector).HasMethod("gin");
            entity.HasIndex(row => row.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
        });

        modelBuilder.Entity<PoiInterestRow>(entity =>
        {
            entity.ToTable("poi_interest", table => table.HasCheckConstraint("ck_poi_interest_weight", "weight > 0 and weight <= 1"));
            entity.HasKey(row => new { row.PoiId, row.TaxonomyCode });
            entity.HasOne<PoiRow>().WithMany(row => row.Interests).HasForeignKey(row => row.PoiId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<TaxonomyNodeRow>().WithMany().HasForeignKey(row => row.TaxonomyCode).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PoiEthicsRow>(entity =>
        {
            entity.ToTable("poi_ethics");
            entity.HasKey(row => row.PoiId);
            entity.HasOne<PoiRow>().WithOne(row => row.Ethics).HasForeignKey<PoiEthicsRow>(row => row.PoiId).OnDelete(DeleteBehavior.Cascade);
            entity.OwnsOne(row => row.CrowdProfile, owned =>
            {
                owned.ToJson("crowd_profile");
                owned.Property(profile => profile.Offpeak).HasJsonPropertyName("offpeak");
                owned.Property(profile => profile.Shoulder).HasJsonPropertyName("shoulder");
                owned.Property(profile => profile.Peak).HasJsonPropertyName("peak");
            });
        });

        modelBuilder.Entity<StoryRow>(entity =>
        {
            entity.ToTable("story", table =>
            {
                table.HasCheckConstraint("ck_story_status", "status in ('published', 'unpublished', 'archived')");
                // Premium text never lives in the public catalog (F-16): a premium story has no text value.
                table.HasCheckConstraint("ck_story_premium_text", "not is_premium or text is null");
            });
            entity.HasKey(row => row.Id);
            entity.HasOne<PoiRow>().WithMany(row => row.Stories).HasForeignKey(row => row.PoiId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(row => new { row.PoiId, row.Lang, row.Kind, row.Version }).IsUnique();
            entity.HasIndex(row => new { row.PoiId, row.Lang }).HasFilter("status = 'published'").HasDatabaseName("ix_story_current_published");
        });

        ApplySnakeCaseColumns(modelBuilder);
    }

    private static void ApplySnakeCaseColumns(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(type => !type.IsMappedToJson()))
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
