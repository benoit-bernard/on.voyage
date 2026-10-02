using OnVoyage.Catalog.Application.Ports;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Factory.Contracts;

namespace OnVoyage.Catalog.Application.IntegrationEvents;

/// <summary>
/// Projects Factory's publication events. Consumption is idempotent: only a higher <c>Version</c> changes anything. After a publication or a
/// withdrawal the Catalog announces its own view of the place (<see cref="PoiProjectionChangedV1"/>) to Discovery and Creators (§13); a
/// redelivered event announces it again, which is harmless (consumers ignore a version they already have) and keeps a crash between the
/// write and the announcement from losing it.
/// </summary>
public static class PoiPublishedHandler
{
    public static async Task<PoiProjectionChangedV1?> Handle(PoiPublishedV1 published, IPoiProjectionWriter writer, CancellationToken cancellationToken)
    {
        await writer.ApplyPublishedAsync(published, cancellationToken);
        return await writer.ProjectionAsync(published.PoiId, cancellationToken);
    }
}

public static class PoiUnpublishedHandler
{
    public static async Task<PoiProjectionChangedV1?> Handle(PoiUnpublishedV1 unpublished, IPoiProjectionWriter writer, CancellationToken cancellationToken)
    {
        await writer.ApplyUnpublishedAsync(unpublished, cancellationToken);
        return await writer.ProjectionAsync(unpublished.PoiId, cancellationToken);
    }
}

public static class StoryPublishedHandler
{
    public static Task Handle(StoryPublishedV1 published, IPoiProjectionWriter writer, CancellationToken cancellationToken) =>
        writer.ApplyStoryPublishedAsync(published, cancellationToken);
}

public static class StoryUnpublishedHandler
{
    public static Task Handle(StoryUnpublishedV1 unpublished, IPoiProjectionWriter writer, CancellationToken cancellationToken) =>
        writer.ApplyStoryUnpublishedAsync(unpublished.StoryId, "unpublished", cancellationToken);
}

public static class StoryArchivedHandler
{
    public static Task Handle(StoryArchivedV1 archived, IPoiProjectionWriter writer, CancellationToken cancellationToken) =>
        writer.ApplyStoryUnpublishedAsync(archived.StoryId, "archived", cancellationToken);
}
