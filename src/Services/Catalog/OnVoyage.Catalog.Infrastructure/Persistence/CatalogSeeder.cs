using Microsoft.EntityFrameworkCore;
using OnVoyage.Catalog.Infrastructure.Seed;
using OnVoyage.Taxonomy;

namespace OnVoyage.Catalog.Infrastructure.Persistence;

/// <summary>
/// Writes the taxonomy and, when asked, the Marseille demo data. Demo data stands in for the Factory pipeline (T-201..T-306)
/// and is only loaded into an empty catalog.
/// </summary>
internal static class CatalogSeeder
{
    public static async Task SeedAsync(CatalogDbContext db, TimeProvider clock, bool includeDemoData, CancellationToken cancellationToken)
    {
        if (!await db.TaxonomyNodes.AnyAsync(cancellationToken))
        {
            db.TaxonomyNodes.AddRange(Interests.All.Select((code, index) => new TaxonomyNodeRow
            {
                Code = code,
                ParentCode = code.Contains('.', StringComparison.Ordinal) ? Interests.LevelOneOf(code) : null,
                Level = (short)(code.Contains('.', StringComparison.Ordinal) ? 2 : 1),
                TaxonomyVersion = Interests.Version,
                SortOrder = index,
            }));
            await db.SaveChangesAsync(cancellationToken);
        }

        if (!includeDemoData || await db.Destinations.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var destination = new DestinationRow
        {
            Id = Guid.CreateVersion7(),
            Slug = MarseilleSeed.Marseille.Slug,
            NameFr = MarseilleSeed.Marseille.Name,
            Center = PoiMapper.ToPoint(MarseilleSeed.Marseille.Center),
            IsActive = true,
        };
        db.Destinations.Add(destination);

        foreach (var poi in MarseilleSeed.Pois)
        {
            db.Pois.Add(new PoiRow
            {
                Id = poi.Id,
                DestinationId = destination.Id,
                Slug = poi.Slug,
                Location = PoiMapper.ToPoint(poi.Location),
                ImportanceScore = (short)Math.Round(poi.Importance * 100),
                HiddenGem = poi.HiddenGem,
                ContentQualityScore = (float)poi.Quality,
                TaxonomyVersion = Interests.Version,
                PublishedAt = now,
                Version = 1,
                Texts = [new PoiTextRow { PoiId = poi.Id, Lang = "fr", Name = poi.Name }],
                Interests = [.. poi.Weights.Select(pair => new PoiInterestRow { PoiId = poi.Id, TaxonomyCode = pair.Key, Weight = (float)pair.Value })],
                Ethics = new PoiEthicsRow
                {
                    PoiId = poi.Id,
                    CrowdProfile = new CrowdProfileJson { Offpeak = (short)Math.Max(1, poi.CrowdLevel - 1), Shoulder = (short)poi.CrowdLevel, Peak = (short)poi.CrowdLevel },
                },
                Stories = [.. poi.Stories.Select(story => new StoryRow
                {
                    Id = story.Id,
                    PoiId = poi.Id,
                    Lang = story.Language,
                    Title = story.Title,
                    Text = story.Text,
                    DurationSeconds = story.DurationSeconds,
                    AudioPath = story.AudioUrl,
                    IsAiGenerated = story.AiGenerated,
                    PublishedAt = now,
                })],
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
