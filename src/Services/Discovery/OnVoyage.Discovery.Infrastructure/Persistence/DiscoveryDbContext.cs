using Microsoft.EntityFrameworkCore;

namespace OnVoyage.Discovery.Infrastructure.Persistence;

internal sealed class DiscoveryDbContext(DbContextOptions<DiscoveryDbContext> options) : DbContext(options)
{
    public const string Schema = "discovery";

    public DbSet<TravelerRow> Travelers => Set<TravelerRow>();
    public DbSet<InterestVectorRow> Vectors => Set<InterestVectorRow>();
    public DbSet<InteractionRow> Interactions => Set<InteractionRow>();
    public DbSet<PoiRatingRow> Ratings => Set<PoiRatingRow>();
    public DbSet<VisitRow> Visits => Set<VisitRow>();
    public DbSet<ImpressionRow> Impressions => Set<ImpressionRow>();
    public DbSet<PoiProjectionRow> Places => Set<PoiProjectionRow>();
    public DbSet<OnboardingClipRow> Clips => Set<OnboardingClipRow>();
    public DbSet<SavedPoiRow> Saved => Set<SavedPoiRow>();
    public DbSet<StoryProjectionRow> Stories => Set<StoryProjectionRow>();
    public DbSet<CfScoreRow> CfScores => Set<CfScoreRow>();
    public DbSet<CategoryAffinityRow> Affinities => Set<CategoryAffinityRow>();
    public DbSet<CreatorProjectionRow> Creators => Set<CreatorProjectionRow>();
    public DbSet<CreatorPlaceLinkRow> CreatorLinks => Set<CreatorPlaceLinkRow>();
    public DbSet<CreatorFollowRow> CreatorFollows => Set<CreatorFollowRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<TravelerRow>(e =>
        {
            e.ToTable("traveler", t => t.HasCheckConstraint("ck_traveler_cohort", "cohort in ('control', 'personalized')"));
            e.HasKey(r => r.Id);
            e.Property(r => r.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<InterestVectorRow>(e =>
        {
            e.ToTable("interest_vector");
            e.HasKey(r => r.TravelerId);
            e.HasOne<TravelerRow>().WithOne().HasForeignKey<InterestVectorRow>(r => r.TravelerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(r => r.Locks).HasColumnType("jsonb");
        });

        modelBuilder.Entity<InteractionRow>(e =>
        {
            e.ToTable("interaction");
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.TravelerId, r.ClientEventId }).IsUnique();
            e.HasIndex(r => new { r.TravelerId, r.OccurredAt });
            e.Property(r => r.Weights).HasColumnType("jsonb");
            e.HasOne<TravelerRow>().WithMany().HasForeignKey(r => r.TravelerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PoiRatingRow>(e =>
        {
            e.ToTable("poi_rating");
            e.HasKey(r => new { r.TravelerId, r.PoiId });
            e.HasOne<TravelerRow>().WithMany().HasForeignKey(r => r.TravelerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VisitRow>(e =>
        {
            e.ToTable("visit");
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.TravelerId, r.ClientEventId }).IsUnique();
            e.HasOne<TravelerRow>().WithMany().HasForeignKey(r => r.TravelerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ImpressionRow>(e =>
        {
            e.ToTable("impression");
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.TravelerId, r.ClientEventId }).IsUnique();
            e.HasIndex(r => r.ShownAt);
            e.HasOne<TravelerRow>().WithMany().HasForeignKey(r => r.TravelerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PoiProjectionRow>(e =>
        {
            e.ToTable("poi_projection");
            e.HasKey(r => r.PoiId);
            e.Property(r => r.Weights).HasColumnType("jsonb");
        });

        modelBuilder.Entity<OnboardingClipRow>(e =>
        {
            e.ToTable("onboarding_clip");
            e.HasKey(r => r.StoryId);
            e.HasIndex(r => r.Lang);
        });

        modelBuilder.Entity<SavedPoiRow>(e =>
        {
            e.ToTable("saved_poi");
            e.HasKey(r => new { r.TravelerId, r.PoiId });
            e.HasOne<TravelerRow>().WithMany().HasForeignKey(r => r.TravelerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StoryProjectionRow>(e =>
        {
            e.ToTable("story_projection");
            e.HasKey(r => r.StoryId);
            e.HasIndex(r => r.PoiId);
            e.Property(r => r.AudioParts).HasColumnType("jsonb");
        });

        modelBuilder.Entity<CfScoreRow>(e =>
        {
            e.ToTable("cf_score", t => t.HasCheckConstraint("ck_cf_score_range", "score between -1 and 1 and support > 0"));
            e.HasKey(r => new { r.TravelerId, r.PoiId });
            e.HasIndex(r => new { r.TravelerId, r.Destination });
            e.HasOne<TravelerRow>().WithMany().HasForeignKey(r => r.TravelerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CategoryAffinityRow>(e =>
        {
            e.ToTable("category_affinity");
            e.HasKey(r => new { r.CodeA, r.CodeB });
        });

        modelBuilder.Entity<CreatorProjectionRow>(e =>
        {
            e.ToTable("creator_projection");
            e.HasKey(r => r.CreatorId);
            e.Property(r => r.Specialties).HasColumnType("jsonb");
            e.Property(r => r.Vector).HasColumnType("jsonb");
        });

        modelBuilder.Entity<CreatorPlaceLinkRow>(e =>
        {
            e.ToTable("creator_place_link");
            e.HasKey(r => new { r.CreatorId, r.PoiId, r.ContentId });
            e.HasIndex(r => r.PoiId);
        });

        modelBuilder.Entity<CreatorFollowRow>(e =>
        {
            e.ToTable("creator_follow");
            e.HasKey(r => new { r.TravelerId, r.CreatorId });
            e.HasOne<TravelerRow>().WithMany().HasForeignKey(r => r.TravelerId).OnDelete(DeleteBehavior.Cascade);
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
