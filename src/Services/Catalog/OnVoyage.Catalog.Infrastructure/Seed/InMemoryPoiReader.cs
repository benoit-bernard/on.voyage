using OnVoyage.Catalog.Application.Ports;
using OnVoyage.Catalog.Domain;

namespace OnVoyage.Catalog.Infrastructure.Seed;

/// <summary>MVP-0 reader backed by the Marseille seed. Replaced by the EF Core/PostGIS reader of T-101 once the database is wired.</summary>
internal sealed class InMemoryPoiReader : IPoiReader
{
    public Task<IReadOnlyList<Poi>> ListPublishedAsync(string destinationSlug, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Poi>>(
            string.Equals(destinationSlug, MarseilleSeed.Marseille.Slug, StringComparison.OrdinalIgnoreCase) ? MarseilleSeed.Pois : []);

    public Task<Poi?> FindBySlugAsync(string slug, CancellationToken cancellationToken) =>
        Task.FromResult(MarseilleSeed.Pois.FirstOrDefault(poi => string.Equals(poi.Slug, slug, StringComparison.OrdinalIgnoreCase)));

    public Task<Destination?> FindDestinationAsync(string slug, CancellationToken cancellationToken) =>
        Task.FromResult<Destination?>(
            string.Equals(slug, MarseilleSeed.Marseille.Slug, StringComparison.OrdinalIgnoreCase) ? MarseilleSeed.Marseille : null);
}
