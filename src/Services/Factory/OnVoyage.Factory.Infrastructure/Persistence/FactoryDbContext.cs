using Microsoft.EntityFrameworkCore;

namespace OnVoyage.Factory.Infrastructure.Persistence;

internal sealed class FactoryDbContext(DbContextOptions<FactoryDbContext> options) : DbContext(options)
{
    public const string Schema = "factory";
    public const string RawSchema = "factory_raw";

    public DbSet<ImportRunRow> ImportRuns => Set<ImportRunRow>();
    public DbSet<PlaceRow> Places => Set<PlaceRow>();
    public DbSet<PlaceInterestRow> PlaceInterests => Set<PlaceInterestRow>();
    public DbSet<DedupLinkRow> DedupLinks => Set<DedupLinkRow>();
    public DbSet<WikidataEntityRow> WikidataEntities => Set<WikidataEntityRow>();
    public DbSet<PageviewsRow> Pageviews => Set<PageviewsRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<ImportRunRow>(entity =>
        {
            entity.ToTable("import_run");
            entity.HasKey(row => row.Id);
        });

        modelBuilder.Entity<PlaceRow>(entity =>
        {
            entity.ToTable("place", table =>
            {
                table.HasCheckConstraint("ck_place_importance", "importance_score is null or importance_score between 0 and 100");
                table.HasCheckConstraint("ck_place_crowd", "crowd_offpeak between 1 and 5 and crowd_shoulder between 1 and 5 and crowd_peak between 1 and 5");
            });
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.OsmType, row.OsmId }).IsUnique();
            entity.HasIndex(row => new { row.DestinationSlug, row.Slug }).IsUnique();
            entity.HasIndex(row => row.Qid);
            entity.HasIndex(row => new { row.DestinationSlug, row.Status });
            entity.HasIndex(row => row.Location).HasMethod("gist");
            entity.HasIndex(row => row.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
            entity.Property(row => row.Location).HasColumnType("geography (point, 4326)");
            entity.Property(row => row.Footprint).HasColumnType("geometry (multipolygon, 4326)");
            entity.Property(row => row.OsmTags).HasColumnType("jsonb");
            entity.HasMany(row => row.Interests).WithOne().HasForeignKey(row => row.PlaceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlaceInterestRow>(entity =>
        {
            entity.ToTable("place_interest", table => table.HasCheckConstraint("ck_place_interest_weight", "weight > 0 and weight <= 1"));
            entity.HasKey(row => new { row.PlaceId, row.TaxonomyCode });
        });

        modelBuilder.Entity<DedupLinkRow>(entity =>
        {
            entity.ToTable("dedup_link");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.KeptPlaceId);
            entity.HasIndex(row => row.OtherPlaceId);
        });

        modelBuilder.Entity<WikidataEntityRow>(entity =>
        {
            entity.ToTable("wikidata_entity", RawSchema);
            entity.HasKey(row => row.Qid);
        });

        modelBuilder.Entity<PageviewsRow>(entity =>
        {
            entity.ToTable("wikipedia_pageviews", RawSchema);
            entity.HasKey(row => new { row.Qid, row.Language });
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
