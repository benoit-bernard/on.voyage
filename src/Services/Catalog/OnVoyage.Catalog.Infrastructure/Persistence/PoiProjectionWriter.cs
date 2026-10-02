using Microsoft.EntityFrameworkCore;
using OnVoyage.Catalog.Application.Ports;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Factory.Contracts;

namespace OnVoyage.Catalog.Infrastructure.Persistence;

internal sealed class PoiProjectionWriter(CatalogDbContext db, TimeProvider clock) : IPoiProjectionWriter
{
    public async Task<bool> ApplyPublishedAsync(PoiPublishedV1 published, CancellationToken cancellationToken)
    {
        var row = await db.Pois.Include(poi => poi.Texts).Include(poi => poi.Interests).Include(poi => poi.Ethics).Include(poi => poi.Links)
            .FirstOrDefaultAsync(poi => poi.Id == published.PoiId, cancellationToken);
        if (row is not null && row.Version >= published.Version)
        {
            return false;
        }

        var destination = await db.Destinations.FirstOrDefaultAsync(item => item.Slug == published.Destination.Slug, cancellationToken);
        if (destination is null)
        {
            destination = new DestinationRow
            {
                Id = Guid.CreateVersion7(),
                Slug = published.Destination.Slug,
                NameFr = published.Destination.Name,
                Center = PoiMapper.ToPoint(new Domain.GeoPoint(published.Destination.Latitude, published.Destination.Longitude)),
                IsActive = true,
            };
            db.Destinations.Add(destination);
        }

        if (row is null)
        {
            row = new PoiRow { Id = published.PoiId, DestinationId = destination.Id, Destination = destination, CreatedAt = clock.GetUtcNow() };
            db.Pois.Add(row);
        }

        row.Slug = await AvailableSlugAsync(published, destination.Id, cancellationToken);
        row.Location = PoiMapper.ToPoint(new Domain.GeoPoint(published.Latitude, published.Longitude));
        row.ImportanceScore = (short)published.ImportanceScore;
        row.HiddenGem = published.HiddenGem;
        row.ContentQualityScore = published.ContentQualityScore;
        row.TaxonomyVersion = published.TaxonomyVersion;
        row.Version = published.Version;
        row.PublishedAt ??= published.OccurredAt;

        row.UpdatedAt = clock.GetUtcNow();

        UpsertText(row, "fr", published.NameFr);
        if (published.NameEn is not null)
        {
            UpsertText(row, "en", published.NameEn);
        }

        foreach (var stale in row.Interests.Where(interest => published.Interests.All(item => item.TaxonomyCode != interest.TaxonomyCode)).ToList())
        {
            db.PoiInterests.Remove(stale);
        }

        foreach (var interest in published.Interests)
        {
            var existing = row.Interests.FirstOrDefault(item => item.TaxonomyCode == interest.TaxonomyCode);
            if (existing is null)
            {
                db.PoiInterests.Add(new PoiInterestRow { PoiId = row.Id, TaxonomyCode = interest.TaxonomyCode, Weight = interest.Weight });
            }
            else
            {
                existing.Weight = interest.Weight;
            }
        }

        // Links are replaced as a whole when the event carries them (an older event without the field leaves them alone).
        if (published.Links is { } links)
        {
            foreach (var stale in row.Links.ToList())
            {
                db.ExternalLinks.Remove(stale);
            }

            foreach (var link in links)
            {
                db.ExternalLinks.Add(new ExternalLinkRow
                {
                    Id = Guid.CreateVersion7(),
                    PoiId = row.Id,
                    Kind = link.Kind,
                    Lang = link.Lang,
                    Url = link.Url,
                    Title = link.Title,
                    Channel = link.Channel,
                    ThumbnailPath = link.ThumbnailPath,
                    VideoId = link.VideoId,
                });
            }
        }

        row.Ethics ??= new PoiEthicsRow { PoiId = row.Id };
        row.Ethics.CrowdProfile = new CrowdProfileJson { Offpeak = (short)published.Crowd.Offpeak, Shoulder = (short)published.Crowd.Shoulder, Peak = (short)published.Crowd.Peak };
        row.Ethics.Fragile = published.Fragile;
        row.Ethics.AccessRegulated = published.AccessRegulated;

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ApplyUnpublishedAsync(PoiUnpublishedV1 unpublished, CancellationToken cancellationToken)
    {
        var row = await db.Pois.FirstOrDefaultAsync(poi => poi.Id == unpublished.PoiId, cancellationToken);
        if (row is null || row.Version >= unpublished.Version)
        {
            return false;
        }

        row.PublishedAt = null;
        row.Version = unpublished.Version;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<PoiProjectionChangedV1?> ProjectionAsync(Guid poiId, CancellationToken cancellationToken)
    {
        var row = await db.Pois.AsNoTracking().Include(poi => poi.Destination).Include(poi => poi.Texts).Include(poi => poi.Interests).Include(poi => poi.Ethics)
            .AsSplitQuery().FirstOrDefaultAsync(poi => poi.Id == poiId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var ethics = row.Ethics;
        var flags = new List<string>();
        if (ethics?.Fragile == true)
        {
            flags.Add("fragile");
        }

        if (ethics?.AccessRegulated == true)
        {
            flags.Add("access_regulated");
        }

        if (row.HiddenGem)
        {
            flags.Add("hidden_gem");
        }

        // The Catalog has no city or alias of its own yet: the destination stands for the city, and there are no aliases (ADR-0016).
        return new PoiProjectionChangedV1(
            Guid.CreateVersion7(),
            clock.GetUtcNow(),
            row.Id,
            row.Version,
            row.DestinationId,
            row.Destination.Slug,
            row.Slug,
            row.Texts.FirstOrDefault(text => text.Lang == "fr")?.Name ?? row.Texts.FirstOrDefault()?.Name ?? row.Slug,
            row.Texts.FirstOrDefault(text => text.Lang == "en")?.Name,
            [],
            row.Destination.NameFr,
            row.Location.Y,
            row.Location.X,
            row.ImportanceScore,
            row.HiddenGem,
            row.ContentQualityScore,
            ethics?.CrowdProfile.Peak ?? 1,
            row.Interests.ToDictionary(interest => interest.TaxonomyCode, interest => interest.Weight),
            flags,
            row.PublishedAt is not null);
    }

    public async Task<bool> ApplyStoryPublishedAsync(StoryPublishedV1 published, CancellationToken cancellationToken)
    {
        if (!await db.Pois.AnyAsync(poi => poi.Id == published.PoiId, cancellationToken))
        {
            throw new PoiNotProjectedException(published.PoiId);
        }

        var row = await db.Stories.FirstOrDefaultAsync(story => story.Id == published.StoryId, cancellationToken);
        if (row is null)
        {
            row = new StoryRow { Id = published.StoryId, PoiId = published.PoiId };
            db.Stories.Add(row);
        }
        else if (row.Status == "published" && row.Version >= published.Version)
        {
            return false; // duplicate delivery
        }

        var main = published.AudioParts.FirstOrDefault(part => part.Part == "main");
        row.Lang = published.Lang;
        row.Kind = published.Kind;
        row.Version = published.Version;
        row.Title = published.Title;
        row.Text = published.IsPremium ? null : published.Text;
        row.DurationSeconds = published.DurationSeconds;
        row.AudioPath = main?.Path;
        row.IsPremium = published.IsPremium;
        row.IsAiGenerated = published.IsAiGenerated;
        row.Status = "published";
        row.PublishedAt = published.OccurredAt;
        row.AudioParts = System.Text.Json.JsonSerializer.Serialize(published.AudioParts.Select(part => new StoryAudioPartJson(part.Part, part.Path, part.Sha256, part.DurationSeconds)));
        row.Sources = System.Text.Json.JsonSerializer.Serialize(published.Sources.Select(source => new StorySourceJson(source.Title, source.Publisher, source.Url, source.License)));

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ApplyStoryUnpublishedAsync(Guid storyId, string status, CancellationToken cancellationToken)
    {
        var row = await db.Stories.FirstOrDefaultAsync(story => story.Id == storyId, cancellationToken);
        if (row is null || row.Status == status)
        {
            return false;
        }

        row.Status = status;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private void UpsertText(PoiRow row, string lang, string name)
    {
        var text = row.Texts.FirstOrDefault(item => item.Lang == lang);
        if (text is null)
        {
            db.PoiTexts.Add(new PoiTextRow { PoiId = row.Id, Lang = lang, Name = name });
        }
        else
        {
            text.Name = name;
        }
    }

    /// <summary>Slugs are unique per destination; if another place (say a demo one) already has this slug, the new one gets a short id suffix.</summary>
    private async Task<string> AvailableSlugAsync(PoiPublishedV1 published, Guid destinationId, CancellationToken cancellationToken)
    {
        var taken = await db.Pois.AnyAsync(poi => poi.DestinationId == destinationId && poi.Slug == published.Slug && poi.Id != published.PoiId, cancellationToken);
        return taken ? $"{published.Slug}-{published.PoiId.ToString("N")[^8..]}" : published.Slug;
    }
}
