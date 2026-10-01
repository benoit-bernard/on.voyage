using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Domain;

namespace OnVoyage.Discovery.Infrastructure.Persistence;

internal sealed class OnboardingStore(DiscoveryDbContext db) : IOnboardingStore
{
    public async Task<IReadOnlyList<StoredClip>> ListAsync(string? lang, CancellationToken cancellationToken)
    {
        var query = from clip in db.Clips.AsNoTracking()
                    join place in db.Places.AsNoTracking() on clip.PoiId equals place.PoiId
                    where place.IsPublished && (lang == null || clip.Lang == lang)
                    select new { clip, place };
        var rows = await query.ToListAsync(cancellationToken);
        return [.. rows
            .OrderBy(r => r.clip.StoryId)
            .Select(r => new StoredClip(
                r.clip.StoryId,
                r.clip.PoiId,
                r.clip.Lang,
                r.clip.Title,
                r.clip.AudioPath,
                r.clip.DurationSeconds,
                r.clip.Active,
                new ClipCandidate(r.clip.StoryId, r.clip.PoiId, r.clip.Lang, JsonSerializer.Deserialize<Dictionary<string, double>>(r.place.Weights) ?? [], r.place.Importance, r.place.Quality)))];
    }

    public async Task<bool> SetActiveAsync(IReadOnlyList<Guid> storyIds, CancellationToken cancellationToken)
    {
        var clips = await db.Clips.Where(c => storyIds.Contains(c.StoryId)).ToListAsync(cancellationToken);
        if (clips.Count != storyIds.Distinct().Count())
        {
            return false;
        }

        var lang = clips.Select(c => c.Lang).Distinct().ToArray();
        foreach (var clip in await db.Clips.Where(c => lang.Contains(c.Lang)).ToListAsync(cancellationToken))
        {
            clip.Active = storyIds.Contains(clip.StoryId);
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

internal sealed class ProjectionWriter(DiscoveryDbContext db) : IProjectionWriter
{
    public async Task ApplyPlaceAsync(PlaceProjection place, CancellationToken cancellationToken)
    {
        var row = await db.Places.FindAsync([place.PoiId], cancellationToken);
        if (row is null)
        {
            row = new PoiProjectionRow { PoiId = place.PoiId };
            db.Places.Add(row);
        }
        else if (row.Version >= place.Version)
        {
            return; // redelivery or an older event
        }

        row.Slug = place.Slug;
        row.Name = place.Name;
        row.Destination = place.Destination;
        row.Latitude = place.Latitude;
        row.Longitude = place.Longitude;
        row.CrowdLevel = place.CrowdLevel;
        row.AccessRegulated = place.AccessRegulated;
        row.Weights = JsonSerializer.Serialize(place.Weights);
        row.Importance = place.Importance;
        row.Quality = place.Quality;
        row.HiddenGem = place.HiddenGem;
        row.Fragile = place.Fragile;
        row.IsPublished = place.IsPublished;
        row.Version = place.Version;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UnpublishPlaceAsync(Guid poiId, int version, CancellationToken cancellationToken)
    {
        var row = await db.Places.FindAsync([poiId], cancellationToken);
        if (row is null)
        {
            db.Places.Add(new PoiProjectionRow { PoiId = poiId, IsPublished = false, Version = version });
        }
        else if (row.Version < version)
        {
            row.IsPublished = false;
            row.Version = version;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ApplyStoryAsync(StoryProjection story, CancellationToken cancellationToken)
    {
        var row = await db.Stories.FindAsync([story.StoryId], cancellationToken);
        if (row is null)
        {
            row = new StoryProjectionRow { StoryId = story.StoryId };
            db.Stories.Add(row);
        }
        else if (row.Version >= story.Version)
        {
            return;
        }

        row.PoiId = story.PoiId;
        row.Lang = story.Lang;
        row.Kind = story.Kind;
        row.DurationSeconds = story.DurationSeconds;
        row.IsPremium = story.IsPremium;
        row.AudioParts = JsonSerializer.Serialize(story.AudioParts);
        row.Version = story.Version;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveStoryAsync(Guid storyId, CancellationToken cancellationToken)
    {
        var row = await db.Stories.FindAsync([storyId], cancellationToken);
        if (row is not null)
        {
            db.Stories.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task ApplyClipAsync(ClipProjection clip, CancellationToken cancellationToken)
    {
        var row = await db.Clips.FindAsync([clip.StoryId], cancellationToken);
        if (row is null)
        {
            row = new OnboardingClipRow { StoryId = clip.StoryId };
            db.Clips.Add(row);
        }
        else if (row.Version >= clip.Version)
        {
            return;
        }

        row.PoiId = clip.PoiId;
        row.Lang = clip.Lang;
        row.Title = clip.Title;
        row.AudioPath = clip.AudioPath;
        row.DurationSeconds = clip.DurationSeconds;
        row.Version = clip.Version;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveClipAsync(Guid storyId, CancellationToken cancellationToken)
    {
        var row = await db.Clips.FindAsync([storyId], cancellationToken);
        if (row is not null)
        {
            db.Clips.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}

internal sealed class ConfiguredMediaUrls(IConfiguration configuration) : IMediaUrls
{
    private string Base => (configuration["Media:PublicBaseUrl"] ?? "/media").TrimEnd('/');

    public string Url(string path) => $"{Base}/{path.TrimStart('/')}";
}
