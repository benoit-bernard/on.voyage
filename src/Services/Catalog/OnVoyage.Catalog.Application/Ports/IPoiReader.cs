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

/// <summary>Local projection of Platform's remote configuration (<c>catalog.config_snapshot</c>).</summary>
public interface IConfigSnapshotStore
{
    /// <summary>Applies a change only if its version is newer; returns false for a duplicate or stale event (idempotent consumption).</summary>
    Task<bool> ApplyAsync(string key, string valueJson, int version, CancellationToken cancellationToken);
}

/// <summary>Write side of the catalog projection of Factory's publication events.</summary>
public interface IPoiProjectionWriter
{
    /// <summary>Applies a publication if it is newer than what the catalog holds; false for a duplicate or stale event.</summary>
    Task<bool> ApplyPublishedAsync(OnVoyage.Factory.Contracts.PoiPublishedV1 published, CancellationToken cancellationToken);

    Task<bool> ApplyUnpublishedAsync(OnVoyage.Factory.Contracts.PoiUnpublishedV1 unpublished, CancellationToken cancellationToken);
}
