using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OnVoyage.Catalog.Contracts;

namespace Catalog.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class CatalogApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:catalogdb", connection);
            builder.UseSetting("Catalog:SeedDemoData", "true");
        });
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Destination_counts_published_places()
    {
        var destination = await _client.GetFromJsonAsync<DestinationDto>("/api/catalog/v1/destinations/marseille", TestContext.Current.CancellationToken);

        destination!.PoiCount.ShouldBe(15);
    }

    [Fact]
    public async Task Pois_are_ordered_by_distance_and_limited_by_postgis()
    {
        // Vieux-Port
        var places = await _client.GetFromJsonAsync<List<PoiSummaryDto>>(
            "/api/catalog/v1/destinations/marseille/pois?lat=43.2951&lon=5.374&radius=2000&limit=5", TestContext.Current.CancellationToken);

        places.ShouldNotBeNull();
        places.Count.ShouldBe(5);
        places[0].Slug.ShouldBe("vieux-port");
        places.Select(p => p.DistanceMeters!.Value).ShouldBe(places.Select(p => p.DistanceMeters!.Value).Order());
        places.ShouldAllBe(p => p.DistanceMeters <= 2000);
    }

    [Fact]
    public async Task Radius_excludes_far_places()
    {
        var places = await _client.GetFromJsonAsync<List<PoiSummaryDto>>(
            "/api/catalog/v1/destinations/marseille/pois?lat=43.2951&lon=5.374&radius=100", TestContext.Current.CancellationToken);

        places!.Select(p => p.Slug).ShouldBe(["vieux-port"]);
    }

    [Fact]
    public async Task Pois_without_position_return_everything_without_distance()
    {
        var places = await _client.GetFromJsonAsync<List<PoiSummaryDto>>("/api/catalog/v1/destinations/marseille/pois", TestContext.Current.CancellationToken);

        places!.Count.ShouldBe(15);
        places.ShouldAllBe(p => p.DistanceMeters == null);
        places.First(p => p.Slug == "vallon-des-auffes").HiddenGem.ShouldBeTrue();
        places.First(p => p.Slug == "calanque-de-sormiou").Weights.ShouldContainKey("nature");
    }

    [Fact]
    public async Task Poi_detail_returns_stories_and_attributions()
    {
        var poi = await _client.GetFromJsonAsync<PoiDetailDto>("/api/catalog/v1/pois/fort-saint-jean", TestContext.Current.CancellationToken);

        poi!.Name.ShouldBe("Fort Saint-Jean");
        poi.Stories.ShouldHaveSingleItem().Text.ShouldNotBeNullOrWhiteSpace();
        poi.Attributions.ShouldContain("© OpenStreetMap contributors");
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
    public async Task Health_includes_the_database()
    {
        var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Premium_stories_cannot_carry_public_text()
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:catalogdb", connection);
            builder.UseSetting("Catalog:SeedDemoData", "true");
        });
        using var _ = factory.CreateClient();

        await using var db = new Npgsql.NpgsqlConnection(connection);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand("update catalog.story set is_premium = true", db);

        var failure = await Should.ThrowAsync<Npgsql.PostgresException>(() => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        failure.ConstraintName.ShouldBe("ck_story_premium_text");
    }
}
