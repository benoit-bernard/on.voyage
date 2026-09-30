using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OnVoyage.Catalog.Contracts;

namespace Catalog.IntegrationTests;

public sealed class CatalogApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Pois_endpoint_returns_places_through_the_wolverine_bus()
    {
        var places = await _client.GetFromJsonAsync<List<PoiSummaryDto>>("/api/catalog/v1/destinations/marseille/pois?lat=43.2951&lon=5.374&limit=3", TestContext.Current.CancellationToken);

        places.ShouldNotBeNull();
        places.Count.ShouldBe(3);
        places[0].DistanceMeters.ShouldNotBeNull();
    }

    [Fact]
    public async Task Unknown_poi_returns_problem_details_with_a_stable_type()
    {
        var response = await _client.GetAsync("/api/catalog/v1/pois/nope", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("https://on.voyage/problems/poi_not_found");
    }

    [Fact]
    public async Task Invalid_coordinates_return_400()
    {
        var response = await _client.GetAsync("/api/catalog/v1/destinations/marseille/pois?lat=999&lon=5", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Health_endpoint_is_available()
    {
        var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
