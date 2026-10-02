using Microsoft.EntityFrameworkCore;
using OnVoyage.Catalog.Application.Ports;
using OnVoyage.Catalog.Domain;

namespace OnVoyage.Catalog.Infrastructure.Persistence;

internal sealed class PostgisPoiReader(CatalogDbContext db, Microsoft.Extensions.Configuration.IConfiguration configuration) : IPoiReader
{
    private string MediaBaseUrl => configuration["Media:PublicBaseUrl"] ?? "/media";

    private const string Lang = "fr";

    public async Task<IReadOnlyList<PlaceDistance>> ListPublishedAsync(
        string destinationSlug, GeoPoint? origin, int radiusMeters, int limit, CancellationToken cancellationToken)
    {
        var published = db.Pois.AsNoTracking().Where(poi => poi.Destination.Slug == destinationSlug && poi.PublishedAt != null);

        List<(Guid Id, double? Distance)> ordered;
        if (origin is null)
        {
            var ids = await published.OrderBy(poi => poi.Slug).Take(limit).Select(poi => poi.Id).ToListAsync(cancellationToken);
            ordered = [.. ids.Select(id => (id, (double?)null))];
        }
        else
        {
            var point = PoiMapper.ToPoint(origin);
            var nearest = await published
                .Where(poi => poi.Location.IsWithinDistance(point, radiusMeters))
                .Select(poi => new { poi.Id, Distance = poi.Location.Distance(point) })
                .OrderBy(item => item.Distance)
                .ThenBy(item => item.Id)
                .Take(limit)
                .ToListAsync(cancellationToken);
            ordered = [.. nearest.Select(item => (item.Id, (double?)item.Distance))];
        }

        var rows = await LoadAsync(db.Pois.AsNoTracking().Where(poi => ordered.Select(item => item.Id).Contains(poi.Id)), cancellationToken);
        var byId = rows.ToDictionary(row => row.Id);
        return [.. ordered.Select(item => new PlaceDistance(PoiMapper.ToDomain(byId[item.Id], Lang, MediaBaseUrl), item.Distance))];
    }

    public async Task<IReadOnlyList<Poi>> SearchPublishedAsync(string destinationSlug, string text, int limit, CancellationToken cancellationToken)
    {
        var ids = await PoiSearch.Build(db, destinationSlug, text, Lang).Take(limit).ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await LoadAsync(db.Pois.AsNoTracking().Where(poi => ids.Contains(poi.Id)), cancellationToken);
        var byId = rows.ToDictionary(row => row.Id);
        return [.. ids.Select(id => PoiMapper.ToDomain(byId[id], Lang, MediaBaseUrl))];
    }

    public Task<int> CountPublishedAsync(string destinationSlug, CancellationToken cancellationToken) =>
        db.Pois.CountAsync(poi => poi.Destination.Slug == destinationSlug && poi.PublishedAt != null, cancellationToken);

    public async Task<Poi?> FindBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        var rows = await LoadAsync(db.Pois.AsNoTracking().Where(poi => poi.Slug == slug && poi.PublishedAt != null).OrderBy(poi => poi.Destination.SortOrder).Take(1), cancellationToken);
        return rows.Count == 0 ? null : PoiMapper.ToDomain(rows[0], Lang, MediaBaseUrl);
    }

    public async Task<Destination?> FindDestinationAsync(string slug, CancellationToken cancellationToken)
    {
        var row = await db.Destinations.AsNoTracking().FirstOrDefaultAsync(destination => destination.Slug == slug && destination.IsActive, cancellationToken);
        return row is null ? null : PoiMapper.ToDomain(row);
    }

    private static Task<List<PoiRow>> LoadAsync(IQueryable<PoiRow> query, CancellationToken cancellationToken) =>
        query.Include(poi => poi.Texts).Include(poi => poi.Interests).Include(poi => poi.Ethics).Include(poi => poi.Stories).Include(poi => poi.Links)
            .AsSplitQuery().ToListAsync(cancellationToken);
}
