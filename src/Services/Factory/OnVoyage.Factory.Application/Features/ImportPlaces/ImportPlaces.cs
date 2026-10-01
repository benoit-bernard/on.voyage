using OnVoyage.Factory.Application.Features.EnrichPlaces;
using OnVoyage.Factory.Application.Ports;

namespace OnVoyage.Factory.Application.Features.ImportPlaces;

public sealed record ImportPlacesCommand(string DestinationSlug);

public sealed record ImportPlacesSummary(string DestinationSlug, int RawRows, int Created, int Updated);

public static class ImportPlacesHandler
{
    /// <summary>Imports the OSM extract, upserts the places, then hands over to enrichment (Wolverine cascades the returned command).</summary>
    public static async Task<(Result<ImportPlacesSummary> Result, EnrichPlacesCommand? Next)> Handle(
        ImportPlacesCommand command, IDestinationCatalog destinations, IOsmImporter importer, IPlaceStore places, CancellationToken cancellationToken)
    {
        var destination = await destinations.FindAsync(command.DestinationSlug, cancellationToken);
        if (destination is null)
        {
            return (Result.Failure<ImportPlacesSummary>("destination_not_found", "Unknown destination."), null);
        }

        var import = await importer.ImportAsync(destination, cancellationToken);
        var (created, updated) = await places.UpsertFromRawAsync(destination.Slug, import.RawTable, cancellationToken);
        return (Result.Success(new ImportPlacesSummary(destination.Slug, import.RowCount, created, updated)), new EnrichPlacesCommand(destination.Slug));
    }
}
