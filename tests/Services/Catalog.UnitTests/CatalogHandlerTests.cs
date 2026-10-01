using FluentValidation;
using NSubstitute;
using OnVoyage.Catalog.Application.Features.GetDestination;
using OnVoyage.Catalog.Application.Features.GetNearbyPois;
using OnVoyage.Catalog.Application.Features.GetPoi;
using OnVoyage.Catalog.Application.Ports;
using OnVoyage.Catalog.Domain;
using OnVoyage.Catalog.Infrastructure.Seed;

namespace Catalog.UnitTests;

public sealed class CatalogHandlerTests
{
    private readonly IPoiReader _reader = Substitute.For<IPoiReader>();
    private readonly IValidator<GetNearbyPoisQuery> _validator = new GetNearbyPoisValidator();

    private static Poi Sample(string slug) => MarseilleSeed.Pois.First(poi => poi.Slug == slug);

    [Fact]
    public async Task Nearby_maps_places_and_rounds_the_distance()
    {
        _reader.ListPublishedAsync("marseille", new GeoPoint(43.2951, 5.374), 1500, 10, Arg.Any<CancellationToken>())
            .Returns([new PlaceDistance(Sample("vieux-port"), 12.6), new PlaceDistance(Sample("mucem"), 900.2)]);

        var result = await GetNearbyPoisHandler.Handle(new GetNearbyPoisQuery("marseille", 43.2951, 5.374, 1500, 10), _reader, _validator, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Select(poi => (poi.Slug, poi.DistanceMeters)).ShouldBe([("vieux-port", 13), ("mucem", 900)]);
    }

    [Fact]
    public async Task Nearby_without_position_asks_the_reader_without_an_origin()
    {
        _reader.ListPublishedAsync("marseille", null, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([new PlaceDistance(Sample("mucem"), null)]);

        var result = await GetNearbyPoisHandler.Handle(new GetNearbyPoisQuery("marseille", null, null), _reader, _validator, CancellationToken.None);

        result.Value!.ShouldHaveSingleItem().DistanceMeters.ShouldBeNull();
    }

    [Theory]
    [InlineData(999d, 5d)]
    [InlineData(43d, 999d)]
    public async Task Nearby_rejects_out_of_range_coordinates(double lat, double lon)
    {
        var result = await GetNearbyPoisHandler.Handle(new GetNearbyPoisQuery("marseille", lat, lon), _reader, _validator, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("validation");
        await _reader.DidNotReceiveWithAnyArgs().ListPublishedAsync(default!, default, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Nearby_rejects_a_lone_latitude()
    {
        var result = await GetNearbyPoisHandler.Handle(new GetNearbyPoisQuery("marseille", 43d, null), _reader, _validator, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Unknown_destination_is_not_found()
    {
        var detail = await GetDestinationHandler.Handle(new GetDestinationQuery("atlantis"), _reader, CancellationToken.None);

        detail.Error!.Code.ShouldBe("destination_not_found");
    }

    [Fact]
    public async Task Poi_detail_returns_stories_and_attributions()
    {
        _reader.FindBySlugAsync("fort-saint-jean", Arg.Any<CancellationToken>()).Returns(Sample("fort-saint-jean"));

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
