using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnVoyage.Discovery.Application.Features;
using OnVoyage.ServiceDefaults.Exports;
using OnVoyage.Taxonomy;

namespace OnVoyage.Discovery.Infrastructure.Persistence;

internal sealed class DataRightsStore(DiscoveryDbContext db, ExportStorage exports) : IDataRightsStore
{
    public async Task<bool> DeleteTravelerAsync(Guid travelerId, CancellationToken cancellationToken)
    {
        // Every table of the traveler hangs off discovery.traveler with ON DELETE CASCADE.
        var deleted = await db.Travelers.Where(t => t.Id == travelerId).ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }

    public async Task<string> ExportAsync(Guid travelerId, CancellationToken cancellationToken)
    {
        var traveler = await db.Travelers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == travelerId, cancellationToken);
        var vector = await db.Vectors.AsNoTracking().FirstOrDefaultAsync(v => v.TravelerId == travelerId, cancellationToken);
        var data = new
        {
            traveler = traveler is null ? null : new { traveler.Lang, traveler.EthicalMode, traveler.ProfileDepth, traveler.Cohort, traveler.CreatedAt },
            interestVector = vector is null ? null : DiscoveryStore.Decode(vector.Vector).OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => Math.Round(p.Value, 4)),
            taxonomyVersion = vector?.TaxonomyVersion ?? Interests.Version,
            locks = vector is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(vector.Locks),
            interactions = await db.Interactions.AsNoTracking().Where(i => i.TravelerId == travelerId).OrderBy(i => i.OccurredAt)
                .Select(i => new { i.ClientEventId, i.Kind, i.PoiId, i.StoryId, i.Value, i.CategoryCode, i.OccurredAt }).ToListAsync(cancellationToken),
            saved = await db.Saved.AsNoTracking().Where(s => s.TravelerId == travelerId).OrderBy(s => s.SavedAt).Select(s => new { s.PoiId, s.SavedAt }).ToListAsync(cancellationToken),
            visits = await db.Visits.AsNoTracking().Where(v => v.TravelerId == travelerId).OrderBy(v => v.VisitedOn).Select(v => new { v.PoiId, v.VisitedOn, v.DwellS, v.Confidence }).ToListAsync(cancellationToken),
            ratings = await db.Ratings.AsNoTracking().Where(r => r.TravelerId == travelerId).Select(r => new { r.PoiId, r.Rating, r.Excluded, r.UpdatedAt }).ToListAsync(cancellationToken),
            follows = await db.CreatorFollows.AsNoTracking().Where(f => f.TravelerId == travelerId && f.Following).OrderBy(f => f.ChangedAt).Select(f => new { f.CreatorId, FollowedAt = f.ChangedAt }).ToListAsync(cancellationToken),
            impressions = await db.Impressions.AsNoTracking().Where(i => i.TravelerId == travelerId).OrderBy(i => i.ShownAt).Select(i => new { i.PoiId, i.Surface, i.ShownAt }).ToListAsync(cancellationToken),
        };

        return JsonSerializer.Serialize(data);
    }

    public Task<string> WritePartAsync(Guid exportId, string json, CancellationToken cancellationToken) => exports.WriteAsync(exportId, "discovery", json, cancellationToken);
}
