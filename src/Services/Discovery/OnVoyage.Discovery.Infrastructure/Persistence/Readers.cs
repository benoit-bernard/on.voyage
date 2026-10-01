using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Taxonomy;

namespace OnVoyage.Discovery.Infrastructure.Persistence;

internal sealed class PlaceReader(DiscoveryDbContext db) : IPlaceReader
{
    public async Task<IReadOnlyList<PlaceInfo>> PlacesAsync(string? destination, CancellationToken cancellationToken)
    {
        var rows = await db.Places.AsNoTracking().Where(p => p.IsPublished && (destination == null || p.Destination == destination)).OrderBy(p => p.PoiId).ToListAsync(cancellationToken);
        return [.. rows.Select(p => new PlaceInfo(
            p.PoiId, p.Slug, p.Name, p.Destination, p.Latitude, p.Longitude,
            JsonSerializer.Deserialize<Dictionary<string, double>>(p.Weights) ?? [],
            p.Importance, p.Quality, p.HiddenGem, p.Fragile, p.AccessRegulated, p.CrowdLevel))];
    }

    public async Task<IReadOnlyList<StoryInfo>> StoriesAsync(string lang, CancellationToken cancellationToken)
    {
        var rows = await db.Stories.AsNoTracking().Where(s => s.Lang == lang).OrderBy(s => s.StoryId).ToListAsync(cancellationToken);
        return [.. rows.Select(s => new StoryInfo(s.StoryId, s.PoiId, s.Lang, s.Kind, s.DurationSeconds, s.IsPremium, JsonSerializer.Deserialize<Dictionary<string, string>>(s.AudioParts) ?? []))];
    }

    public async Task<IReadOnlyDictionary<Guid, int>> ImpressionsSinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        await db.Impressions.AsNoTracking().Where(i => i.ShownAt >= since).GroupBy(i => i.PoiId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

    public async Task<(string Slug, string Name, double Latitude, double Longitude)?> DestinationAsync(string slug, CancellationToken cancellationToken)
    {
        // The projection holds places, not destinations: the centre is the mean of the destination's places, the name comes from configuration of the slug.
        var places = await db.Places.AsNoTracking().Where(p => p.IsPublished && p.Destination == slug).Select(p => new { p.Latitude, p.Longitude }).ToListAsync(cancellationToken);
        if (places.Count == 0)
        {
            return null;
        }

        return (slug, DestinationNames.NameOf(slug), places.Average(p => p.Latitude), places.Average(p => p.Longitude));
    }
}

internal static class DestinationNames
{
    public static string NameOf(string slug) => string.Join(' ', slug.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}

internal sealed class TravelerReader(DiscoveryDbContext db) : ITravelerReader
{
    public async Task<TravelerInfo?> GetAsync(Guid travelerId, CancellationToken cancellationToken)
    {
        var t = await db.Travelers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == travelerId, cancellationToken);
        return t is null ? null : new TravelerInfo(t.Id, t.Lang, t.EthicalMode, t.IsPremium, t.ProfileDepth, t.Cohort);
    }

    public async Task<IReadOnlyDictionary<Guid, double>> RatingsAsync(Guid travelerId, CancellationToken cancellationToken) =>
        await db.Ratings.AsNoTracking().Where(r => r.TravelerId == travelerId).ToDictionaryAsync(r => r.PoiId, r => r.Rating, cancellationToken);

    public async Task<IReadOnlyList<(Guid PoiId, DateTimeOffset SavedAt)>> SavedAsync(Guid travelerId, CancellationToken cancellationToken) =>
        [.. (await db.Saved.AsNoTracking().Where(s => s.TravelerId == travelerId).ToListAsync(cancellationToken)).Select(s => (s.PoiId, s.SavedAt))];

    public async Task<IReadOnlyList<HistoryEntry>> HistoryAsync(Guid travelerId, CancellationToken cancellationToken)
    {
        var rows = await db.Interactions.AsNoTracking().Where(i => i.TravelerId == travelerId && i.PoiId != null).Select(i => new { PoiId = i.PoiId!.Value, i.Kind, i.OccurredAt }).ToListAsync(cancellationToken);
        return [.. rows.GroupBy(r => r.PoiId).Select(g => new HistoryEntry(
            g.Key,
            g.Max(r => r.OccurredAt),
            g.Any(r => r.Kind is "listen_80" or "replay" or "like" or "meh" or "dislike_poi" or "abandon_early"),
            g.Any(r => r.Kind == "visit")))];
    }

    public async Task<IReadOnlyList<Guid>> RecentSurprisesAsync(Guid travelerId, int count, CancellationToken cancellationToken) =>
        await db.Impressions.AsNoTracking().Where(i => i.TravelerId == travelerId && i.Surface == "surprise").OrderByDescending(i => i.ShownAt).Select(i => i.PoiId).Take(count).ToListAsync(cancellationToken);
}

internal sealed class AffinityStore(DiscoveryDbContext db) : IAffinityStore
{
    public const int MinProfileDepth = 10;
    private const double LikedThreshold = 0.5;

    public async Task<(IReadOnlyDictionary<(string A, string B), double> Lifts, int Travelers)> LoadAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Affinities.AsNoTracking().ToListAsync(cancellationToken);
        var travelers = await db.Vectors.AsNoTracking().CountAsync(v => db.Travelers.Any(t => t.Id == v.TravelerId && t.ProfileDepth >= MinProfileDepth), cancellationToken);
        return (rows.ToDictionary(r => (r.CodeA, r.CodeB), r => r.Lift), travelers);
    }

    /// <summary>
    /// <c>lift(a, b) = P(b liked | a liked) / P(b liked)</c> over travelers whose profile holds at least ten points (neighbours' eligibility, §6.13),
    /// "liked" meaning an affinity of 0.5 or more. Pairs liked together by fewer than three travelers are left out: a lift on two people is noise.
    /// </summary>
    public async Task<int> RecomputeAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var vectors = await db.Vectors.AsNoTracking()
            .Where(v => db.Travelers.Any(t => t.Id == v.TravelerId && t.ProfileDepth >= MinProfileDepth))
            .Select(v => v.Vector)
            .ToListAsync(cancellationToken);
        var index = Interests.All.Select((code, i) => (code, i)).ToArray();
        var liked = vectors.Select(v => index.Where(x => x.i < v.Length && v[x.i] >= LikedThreshold).Select(x => x.code).ToHashSet()).ToList();
        var n = liked.Count;

        var fresh = new List<CategoryAffinityRow>();
        if (n > 0)
        {
            var single = Interests.All.ToDictionary(code => code, code => liked.Count(set => set.Contains(code)));
            foreach (var a in Interests.All.Where(code => single[code] > 0))
            {
                foreach (var b in Interests.All.Where(code => code != a && single[code] > 0))
                {
                    var both = liked.Count(set => set.Contains(a) && set.Contains(b));
                    if (both < 3)
                    {
                        continue;
                    }

                    var lift = ((double)both / single[a]) / ((double)single[b] / n);
                    fresh.Add(new CategoryAffinityRow { CodeA = a, CodeB = b, Lift = Math.Round(lift, 4), ComputedAt = now });
                }
            }
        }

        await db.Affinities.ExecuteDeleteAsync(cancellationToken);
        db.Affinities.AddRange(fresh);
        await db.SaveChangesAsync(cancellationToken);
        return n;
    }
}
