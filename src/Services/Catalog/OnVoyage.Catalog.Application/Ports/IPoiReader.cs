using OnVoyage.Catalog.Domain;

namespace OnVoyage.Catalog.Application.Ports;

public sealed record PlaceDistance(Poi Poi, double? DistanceMeters);

public interface IPoiReader
{
    /// <summary>Published places of a destination, nearest first when an origin is given. The origin is only used to filter and order; it is never stored.</summary>
    Task<IReadOnlyList<PlaceDistance>> ListPublishedAsync(string destinationSlug, GeoPoint? origin, int radiusMeters, int limit, CancellationToken cancellationToken);

    Task<int> CountPublishedAsync(string destinationSlug, CancellationToken cancellationToken);

    Task<Poi?> FindBySlugAsync(string slug, CancellationToken cancellationToken);

    Task<Destination?> FindDestinationAsync(string slug, CancellationToken cancellationToken);
}
