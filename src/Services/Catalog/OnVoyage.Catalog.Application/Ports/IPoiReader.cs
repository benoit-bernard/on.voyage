using OnVoyage.Catalog.Domain;

namespace OnVoyage.Catalog.Application.Ports;

public interface IPoiReader
{
    Task<IReadOnlyList<Poi>> ListPublishedAsync(string destinationSlug, CancellationToken cancellationToken);

    Task<Poi?> FindBySlugAsync(string slug, CancellationToken cancellationToken);

    Task<Destination?> FindDestinationAsync(string slug, CancellationToken cancellationToken);
}
