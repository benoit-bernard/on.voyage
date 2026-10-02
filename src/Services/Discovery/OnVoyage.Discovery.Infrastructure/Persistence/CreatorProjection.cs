using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Recommendation.Engine;

namespace OnVoyage.Discovery.Infrastructure.Persistence;

/// <summary>
/// The creator vector of §6.15: <c>c = Σ w_i · p_i / Σ w_i</c> over the creator's validated, published places, <c>w_i = 1</c> (+0.5 with a tip;
/// +0.5 in an itinerary arrives with lists, T-1210). Recomputed whenever a link or a place changes, so the order of events does not matter.
/// </summary>
internal static class CreatorVectors
{
    public const double TipBonus = 0.5;
    public const string TipKind = "tip";

    public static async Task RecomputeAsync(DiscoveryDbContext db, IReadOnlyCollection<Guid> creatorIds, CancellationToken cancellationToken)
    {
        foreach (var creatorId in creatorIds)
        {
            var creator = await db.Creators.FindAsync([creatorId], cancellationToken);
            if (creator is null)
            {
                continue; // the vector is computed when the creator arrives
            }

            var links = await db.CreatorLinks.AsNoTracking().Where(l => l.CreatorId == creatorId && l.Validated).ToListAsync(cancellationToken);
            var poiIds = links.Select(l => l.PoiId).Distinct().ToArray();
            var places = await db.Places.AsNoTracking().Where(p => poiIds.Contains(p.PoiId) && p.IsPublished).Select(p => new { p.PoiId, p.Weights }).ToListAsync(cancellationToken);

            var vector = CreatorAffinity.Vector(places.Select(place =>
            {
                var tip = links.Any(l => l.PoiId == place.PoiId && l.Kind == TipKind);
                return ((IReadOnlyDictionary<string, double>)(JsonSerializer.Deserialize<Dictionary<string, double>>(place.Weights) ?? []), 1d + (tip ? TipBonus : 0d));
            }));

            var json = JsonSerializer.Serialize(vector.ToDictionary(pair => pair.Key, pair => Math.Round(pair.Value, 6)));
            if (creator.Vector != json)
            {
                creator.Vector = json;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    public static async Task RecomputeForPlaceAsync(DiscoveryDbContext db, Guid poiId, CancellationToken cancellationToken)
    {
        var creators = await db.CreatorLinks.AsNoTracking().Where(l => l.PoiId == poiId && l.Validated).Select(l => l.CreatorId).Distinct().ToListAsync(cancellationToken);
        await RecomputeAsync(db, creators, cancellationToken);
    }
}

internal sealed class CreatorProjectionWriter(DiscoveryDbContext db) : ICreatorProjectionWriter
{
    public async Task ApplyCreatorAsync(CreatorProjection creator, CancellationToken cancellationToken)
    {
        var row = await db.Creators.FindAsync([creator.CreatorId], cancellationToken);
        if (row is null)
        {
            row = new CreatorProjectionRow { CreatorId = creator.CreatorId };
            db.Creators.Add(row);
        }
        else if (row.OccurredAt > creator.OccurredAt)
        {
            return; // an older event
        }

        row.Handle = creator.Handle;
        row.IsPublished = creator.IsPublished;
        row.OccurredAt = creator.OccurredAt;
        if (creator.IsPublished)
        {
            // Unpublishing keeps the last public profile; only the flag changes.
            row.DisplayName = creator.DisplayName;
            row.AvatarPath = creator.AvatarPath;
            row.Specialties = JsonSerializer.Serialize(creator.Specialties);
        }

        await db.SaveChangesAsync(cancellationToken);
        await CreatorVectors.RecomputeAsync(db, [creator.CreatorId], cancellationToken);
    }

    public async Task ApplyLinkAsync(CreatorLinkProjection link, CancellationToken cancellationToken)
    {
        var contentId = link.ContentId ?? Guid.Empty;
        var row = await db.CreatorLinks.FindAsync([link.CreatorId, link.PoiId, contentId], cancellationToken);
        if (row is null)
        {
            row = new CreatorPlaceLinkRow { CreatorId = link.CreatorId, PoiId = link.PoiId, ContentId = contentId };
            db.CreatorLinks.Add(row);
        }
        else if (row.OccurredAt > link.OccurredAt)
        {
            return;
        }

        row.Kind = link.Kind;
        row.IsCommercial = link.IsCommercial;
        row.Validated = link.Validated;
        row.OccurredAt = link.OccurredAt;
        await db.SaveChangesAsync(cancellationToken);
        await CreatorVectors.RecomputeAsync(db, [link.CreatorId], cancellationToken);
    }
}

internal sealed class CreatorReader(DiscoveryDbContext db) : ICreatorReader
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<CreatorOnPlaceInfo>>> OnPlacesAsync(string? destination, CancellationToken cancellationToken)
    {
        var rows = await Visible(destination).ToListAsync(cancellationToken);
        return rows
            .GroupBy(r => r.PoiId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CreatorOnPlaceInfo>)[.. g
                    .GroupBy(r => r.CreatorId)
                    .Select(c => new CreatorOnPlaceInfo(c.Key, c.First().Handle, ParseVector(c.First().Vector), c.All(r => r.IsCommercial)))
                    .OrderBy(c => c.Handle, StringComparer.Ordinal)]);
    }

    public async Task<IReadOnlyList<CreatorInfo>> CreatorsAsync(string? destination, CancellationToken cancellationToken)
    {
        var rows = await Visible(destination).ToListAsync(cancellationToken);
        return [.. rows
            .GroupBy(r => r.CreatorId)
            .Select(g => new CreatorInfo(
                g.Key,
                g.First().Handle,
                g.First().DisplayName,
                g.First().AvatarPath,
                JsonSerializer.Deserialize<string[]>(g.First().Specialties) ?? [],
                ParseVector(g.First().Vector),
                g.Select(r => r.PoiId).Distinct().Count()))
            .OrderBy(c => c.Handle, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlySet<Guid>> FollowedAsync(Guid travelerId, CancellationToken cancellationToken) =>
        (await db.CreatorFollows.AsNoTracking().Where(f => f.TravelerId == travelerId && f.Following).Select(f => f.CreatorId).ToListAsync(cancellationToken)).ToHashSet();

    private static Dictionary<string, double> ParseVector(string json) => JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? [];

    /// <summary>Validated links of published creators to published places (of the destination, when given), with the creator's public fields.</summary>
    private IQueryable<VisibleLink> Visible(string? destination) =>
        from link in db.CreatorLinks.AsNoTracking()
        join creator in db.Creators.AsNoTracking() on link.CreatorId equals creator.CreatorId
        join place in db.Places.AsNoTracking() on link.PoiId equals place.PoiId
        where link.Validated && creator.IsPublished && place.IsPublished && (destination == null || place.Destination == destination)
        select new VisibleLink(link.CreatorId, link.PoiId, link.IsCommercial, creator.Handle, creator.DisplayName, creator.AvatarPath, creator.Specialties, creator.Vector);

    private sealed record VisibleLink(Guid CreatorId, Guid PoiId, bool IsCommercial, string Handle, string DisplayName, string? AvatarPath, string Specialties, string Vector);
}
