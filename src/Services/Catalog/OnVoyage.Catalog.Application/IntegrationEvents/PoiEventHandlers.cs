using OnVoyage.Catalog.Application.Ports;
using OnVoyage.Factory.Contracts;

namespace OnVoyage.Catalog.Application.IntegrationEvents;

/// <summary>Projects Factory's publication events. Consumption is idempotent: only a higher <c>Version</c> changes anything.</summary>
public static class PoiPublishedHandler
{
    public static Task Handle(PoiPublishedV1 published, IPoiProjectionWriter writer, CancellationToken cancellationToken) =>
        writer.ApplyPublishedAsync(published, cancellationToken);
}

public static class PoiUnpublishedHandler
{
    public static Task Handle(PoiUnpublishedV1 unpublished, IPoiProjectionWriter writer, CancellationToken cancellationToken) =>
        writer.ApplyUnpublishedAsync(unpublished, cancellationToken);
}
