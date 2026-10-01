using FluentValidation;
using OnVoyage.Catalog.Application.Ports;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Catalog.Domain;

namespace OnVoyage.Catalog.Application.Features.GetNearbyPois;

/// <summary>Published places of a destination. Coordinates are optional and only used to compute the distance; never stored or logged.</summary>
public sealed record GetNearbyPoisQuery(string Destination, double? Latitude, double? Longitude, int RadiusMeters = 50_000, int Limit = 100);

public sealed class GetNearbyPoisValidator : AbstractValidator<GetNearbyPoisQuery>
{
    public GetNearbyPoisValidator()
    {
        RuleFor(query => query.Destination).NotEmpty().MaximumLength(64);
        RuleFor(query => query.Latitude).InclusiveBetween(-90d, 90d).When(query => query.Latitude.HasValue);
        RuleFor(query => query.Longitude).InclusiveBetween(-180d, 180d).When(query => query.Longitude.HasValue);
        RuleFor(query => query.Latitude.HasValue).Equal(true).When(query => query.Longitude.HasValue).WithMessage("latitude and longitude go together");
        RuleFor(query => query.Longitude.HasValue).Equal(true).When(query => query.Latitude.HasValue).WithMessage("latitude and longitude go together");
        RuleFor(query => query.RadiusMeters).InclusiveBetween(50, 200_000);
        RuleFor(query => query.Limit).InclusiveBetween(1, 500);
    }
}

public static class GetNearbyPoisHandler
{
    public static async Task<Result<IReadOnlyList<PoiSummaryDto>>> Handle(
        GetNearbyPoisQuery query,
        IPoiReader reader,
        IValidator<GetNearbyPoisQuery> validator,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(query, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure<IReadOnlyList<PoiSummaryDto>>("validation", validation.Errors[0].ErrorMessage);
        }

        var origin = query is { Latitude: { } lat, Longitude: { } lon } ? new GeoPoint(lat, lon) : null;
        var places = await reader.ListPublishedAsync(query.Destination, origin, query.RadiusMeters, query.Limit, cancellationToken);
        var summaries = places.Select(place => Map(place.Poi, place.DistanceMeters)).ToArray();

        return Result.Success<IReadOnlyList<PoiSummaryDto>>(summaries);
    }

    internal static PoiSummaryDto Map(Poi poi, double? distance) => new(
        poi.Id,
        poi.Slug,
        poi.Name,
        poi.Category,
        poi.Location.Latitude,
        poi.Location.Longitude,
        poi.Importance,
        poi.Quality,
        poi.CrowdLevel,
        poi.HiddenGem,
        distance is { } meters ? (int)Math.Round(meters) : null,
        poi.Stories.Count > 0 ? poi.Stories[0].DurationSeconds : null,
        poi.Weights);
}
