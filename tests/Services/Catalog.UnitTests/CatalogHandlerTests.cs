using FluentValidation;
using OnVoyage.Catalog.Application.Features.GetDestination;
using OnVoyage.Catalog.Application.Features.GetNearbyPois;
using OnVoyage.Catalog.Application.Features.GetPoi;
using OnVoyage.Catalog.Infrastructure.Seed;

namespace Catalog.UnitTests;

public sealed class CatalogHandlerTests
{
    private readonly InMemoryPoiReader _reader = new();
    private readonly IValidator<GetNearbyPoisQuery> _validator = new GetNearbyPoisValidator();

    [Fact]
    public async Task Nearby_without_position_lists_all_published_places_without_distance()
    {
        var result = await GetNearbyPoisHandler.Handle(new GetNearbyPoisQuery("marseille", null, null), _reader, _validator, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Count.ShouldBe(15);
        result.Value.ShouldAllBe(poi => poi.DistanceMeters == null);
    }

    [Fact]
    public async Task Nearby_with_position_sorts_by_distance_and_honors_radius()
    {
        // Vieux-Port
        var result = await GetNearbyPoisHandler.Handle(new GetNearbyPoisQuery("marseille", 43.2951, 5.3740, 1500), _reader, _validator, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var places = result.Value!;
        places[0].Slug.ShouldBe("vieux-port");
        places.Select(p => p.DistanceMeters!.Value).ShouldBe(places.Select(p => p.DistanceMeters!.Value).Order());
        places.ShouldAllBe(p => p.DistanceMeters <= 1500);
    }

    [Theory]
    [InlineData(999d, 5d)]
    [InlineData(43d, 999d)]
    public async Task Nearby_rejects_out_of_range_coordinates(double lat, double lon)
    {
        var result = await GetNearbyPoisHandler.Handle(new GetNearbyPoisQuery("marseille", lat, lon), _reader, _validator, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("validation");
    }

    [Fact]
    public async Task Nearby_rejects_a_lone_latitude()
    {
        var result = await GetNearbyPoisHandler.Handle(new GetNearbyPoisQuery("marseille", 43d, null), _reader, _validator, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Unknown_destination_is_empty_and_not_found_on_detail()
    {
        var list = await GetNearbyPoisHandler.Handle(new GetNearbyPoisQuery("atlantis", null, null), _reader, _validator, CancellationToken.None);
        var detail = await GetDestinationHandler.Handle(new GetDestinationQuery("atlantis"), _reader, CancellationToken.None);

        list.Value.ShouldBeEmpty();
        detail.Error!.Code.ShouldBe("destination_not_found");
    }

    [Fact]
    public async Task Poi_detail_returns_stories_and_attributions()
    {
        var result = await GetPoiHandler.Handle(new GetPoiQuery("fort-saint-jean"), _reader, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Stories.ShouldNotBeEmpty();
        result.Value.Attributions.ShouldContain("© OpenStreetMap contributors");
    }

    [Fact]
    public async Task Missing_poi_is_not_found()
    {
        var result = await GetPoiHandler.Handle(new GetPoiQuery("nope"), _reader, CancellationToken.None);

        result.Error!.Code.ShouldBe("poi_not_found");
    }

    [Fact]
    public void Seed_places_carry_level_one_weights_and_valid_taxonomy_codes()
    {
        var valid = OnVoyage.Taxonomy.Interests.All.ToHashSet();

        foreach (var poi in MarseilleSeed.Pois)
        {
            poi.Weights.Keys.ShouldAllBe(code => valid.Contains(code), poi.Slug);
            poi.Weights.ContainsKey(OnVoyage.Taxonomy.Interests.LevelOneOf(poi.Weights.Keys.First())).ShouldBeTrue(poi.Slug);
            poi.Weights.Values.ShouldAllBe(w => w > 0 && w <= 1);
        }
    }
}
