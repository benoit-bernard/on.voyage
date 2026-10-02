using OnVoyage.Catalog.Contracts;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Domain;

namespace OnVoyage.Creators.Application.IntegrationEvents;

/// <summary>
/// Keeps <c>creators.poi_directory</c> in step with the Catalog (§13). The coordinates of the event are dropped here: Creators matches and
/// searches by name and never needs a position. Consumption is idempotent: only a higher <c>Version</c> changes anything.
/// </summary>
public static class PoiProjectionChangedHandler
{
    public static Task Handle(PoiProjectionChangedV1 changed, IPoiDirectoryWriter writer, CancellationToken cancellationToken) =>
        writer.ApplyAsync(
            new PoiEntry(changed.PoiId, changed.DestinationId, changed.DestinationSlug, changed.NameFr, changed.NameEn, changed.Aliases, changed.City, changed.ImportanceScore, changed.IsPublished, changed.Version),
            cancellationToken);
}
