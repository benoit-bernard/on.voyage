using OnVoyage.Catalog.Domain;

namespace OnVoyage.Catalog.Application.Ports;

public sealed record PlaceDistance(Poi Poi, double? DistanceMeters);

public interface IPoiReader
{
    /// <summary>Published places of a destination, nearest first when an origin is given. The origin is only used to filter and order; it is never stored.</summary>
    Task<IReadOnlyList<PlaceDistance>> ListPublishedAsync(string destinationSlug, GeoPoint? origin, int radiusMeters, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Published places whose name, description or keywords match <paramref name="text"/> (accent- and case-insensitive, tolerant to a typo), best match first.
    /// The text is only used to build the query.
    /// </summary>
    Task<IReadOnlyList<Poi>> SearchPublishedAsync(string destinationSlug, string text, int limit, CancellationToken cancellationToken);

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

    /// <summary>Applies a story publication; false when stale. Throws <see cref="PoiNotProjectedException"/> if the place has not arrived yet, so the message is retried.</summary>
    Task<bool> ApplyStoryPublishedAsync(OnVoyage.Factory.Contracts.StoryPublishedV1 published, CancellationToken cancellationToken);

    Task<bool> ApplyStoryUnpublishedAsync(Guid storyId, string status, CancellationToken cancellationToken);

    /// <summary>
    /// The projection event for the place as the catalog holds it now (<c>IsPublished</c> false once withdrawn), or null when the place is unknown.
    /// Built from the stored state, so repeating it after a redelivery gives the same event.
    /// </summary>
    Task<OnVoyage.Catalog.Contracts.PoiProjectionChangedV1?> ProjectionAsync(Guid poiId, CancellationToken cancellationToken);
}

/// <summary>A story event arrived before the place it belongs to; events of different queues are not ordered.</summary>
public sealed class PoiNotProjectedException(Guid poiId) : Exception($"Place {poiId} is not in the catalog yet.");
