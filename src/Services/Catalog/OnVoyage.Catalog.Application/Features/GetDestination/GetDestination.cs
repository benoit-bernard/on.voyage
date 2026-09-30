using OnVoyage.Catalog.Application.Ports;
using OnVoyage.Catalog.Contracts;

namespace OnVoyage.Catalog.Application.Features.GetDestination;

public sealed record GetDestinationQuery(string Slug);

public static class GetDestinationHandler
{
    public static async Task<Result<DestinationDto>> Handle(GetDestinationQuery query, IPoiReader reader, CancellationToken cancellationToken)
    {
        var destination = await reader.FindDestinationAsync(query.Slug, cancellationToken);
        if (destination is null)
        {
            return Result.Failure<DestinationDto>("destination_not_found", "Destination not found.");
        }

        var pois = await reader.ListPublishedAsync(destination.Slug, cancellationToken);
        return Result.Success(new DestinationDto(destination.Slug, destination.Name, destination.Center.Latitude, destination.Center.Longitude, pois.Count));
    }
}
