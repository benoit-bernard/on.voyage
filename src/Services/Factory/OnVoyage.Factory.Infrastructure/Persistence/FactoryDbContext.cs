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
    public DbSet<SourceDocumentRow> SourceDocuments => Set<SourceDocumentRow>();
    public DbSet<FactRow> Facts => Set<FactRow>();
    public DbSet<StoryRow> Stories => Set<StoryRow>();
    public DbSet<StoryAudioPartRow> StoryAudioParts => Set<StoryAudioPartRow>();
    public DbSet<StoryReportRow> StoryReports => Set<StoryReportRow>();
    public DbSet<PronunciationRow> Pronunciations => Set<PronunciationRow>();
    public DbSet<GenerationBatchRow> GenerationBatches => Set<GenerationBatchRow>();

    public DbSet<GenerationJobRow> GenerationJobs => Set<GenerationJobRow>();

    public DbSet<AuditLogRow> AuditLog => Set<AuditLogRow>();

    public DbSet<LlmCallRow> LlmCalls => Set<LlmCallRow>();

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

        modelBuilder.Entity<SourceDocumentRow>(entity =>
        {
            entity.ToTable("source_document");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.PlaceId, row.Url });
        });

        modelBuilder.Entity<FactRow>(entity =>
        {
            entity.ToTable("fact", table => table.HasCheckConstraint("ck_fact_confidence", "confidence between 0 and 1"));
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.PlaceId);
            entity.HasIndex(row => row.DocumentId);
        });

        modelBuilder.Entity<StoryRow>(entity =>
        {
            entity.ToTable("story");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.PlaceId, row.Lang, row.Kind, row.Version }).IsUnique();
            entity.Property(row => row.CheckReport).HasColumnType("jsonb");
        });

        modelBuilder.Entity<StoryAudioPartRow>(entity =>
        {
            entity.ToTable("story_audio_part");
            entity.HasKey(row => new { row.StoryId, row.Part });
        });

        modelBuilder.Entity<StoryReportRow>(entity =>
        {
            entity.ToTable("story_report");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.StoryId, row.TravelerId }).IsUnique();
            entity.HasIndex(row => new { row.TravelerId, row.CreatedAt });
        });

        modelBuilder.Entity<PronunciationRow>(entity =>
        {
            entity.ToTable("pronunciation");
            entity.HasKey(row => new { row.DestinationSlug, row.Term });
        });

        modelBuilder.Entity<GenerationBatchRow>(entity =>
        {
            entity.ToTable("generation_batch");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Criteria).HasColumnType("jsonb");
            entity.HasIndex(row => row.CreatedAt);
        });

        modelBuilder.Entity<GenerationJobRow>(entity =>
        {
            entity.ToTable("generation_job", table => table.HasCheckConstraint("ck_generation_job_state", "state in ('Pending', 'Running', 'Succeeded', 'Failed')"));
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.BatchId, row.State });
        });

        modelBuilder.Entity<AuditLogRow>(entity =>
        {
            entity.ToTable("audit_log");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.At);
        });

        modelBuilder.Entity<LlmCallRow>(entity =>
        {
            entity.ToTable("llm_call");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.CreatedAt);
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
