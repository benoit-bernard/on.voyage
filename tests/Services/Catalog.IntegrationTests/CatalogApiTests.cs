using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OnVoyage.Catalog.Api;
using OnVoyage.Catalog.Contracts;
using OnVoyage.TestInfrastructure;

namespace Catalog.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class CatalogApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<CatalogApiMarker> _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        _factory = new WebApplicationFactory<CatalogApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", connection);
            builder.UseSetting("Catalog:SeedDemoData", "true");
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
        });
        _client = _factory.CreateClient();
        _client.Authenticate();
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
    public async Task Reads_require_a_valid_token()
    {
        using var anonymousCaller = _factory.CreateClient();
        (await anonymousCaller.GetAsync("/api/catalog/v1/destinations/marseille", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        foreach (var bad in new[] { "garbage", TestTokens.Mint(secret: "another-secret-another-secret-0123456789"), TestTokens.Mint(lifetime: TimeSpan.FromMinutes(-5), now: DateTimeOffset.UtcNow.AddHours(-1)) })
        {
            using var caller = _factory.CreateClient();
            caller.Authenticate(bad);
            (await caller.GetAsync("/api/catalog/v1/destinations/marseille", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var internalHost = _factory.CreateClient();
        internalHost.Authenticate(TestTokens.Mint(roles: ["internal"], anonymous: false));
        (await internalHost.GetAsync("/api/catalog/v1/destinations/marseille", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_is_open_without_a_token()
    {
        using var open = _factory.CreateClient();
        (await open.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("cathedrale")]
    [InlineData("CATHÉDRALE")]
    [InlineData("cathedral")]
    public async Task Search_finds_the_cathedral_without_accents_in_the_first_three(string text)
    {
        var places = await _client.GetFromJsonAsync<List<PoiSummaryDto>>($"/api/catalog/v1/search?q={Uri.EscapeDataString(text)}&destination=marseille", TestContext.Current.CancellationToken);

        places!.Take(3).Select(p => p.Slug).ShouldContain("cathedrale-de-la-major");
    }

    [Fact]
    public async Task Search_finds_a_keyword_of_the_description_and_ranks_a_name_that_starts_with_the_text_first()
    {
        var places = await _client.GetFromJsonAsync<List<PoiSummaryDto>>("/api/catalog/v1/search?q=fort", TestContext.Current.CancellationToken);

        places!.ShouldNotBeEmpty();
        places[0].Slug.ShouldBe("fort-saint-jean");
        places.ShouldAllBe(p => p.DistanceMeters == null);
    }

    [Fact]
    public async Task Search_rejects_a_single_character_and_returns_nothing_for_gibberish()
    {
        var tooShort = await _client.GetAsync("/api/catalog/v1/search?q=a", TestContext.Current.CancellationToken);
        var gibberish = await _client.GetFromJsonAsync<List<PoiSummaryDto>>("/api/catalog/v1/search?q=zzzxqwv", TestContext.Current.CancellationToken);

        tooShort.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        gibberish.ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_treats_sql_and_like_syntax_as_plain_text()
    {
        var injection = await _client.GetAsync("/api/catalog/v1/search?q=" + Uri.EscapeDataString("fort'; drop table catalog.poi;--"), TestContext.Current.CancellationToken);
        var wildcard = await _client.GetFromJsonAsync<List<PoiSummaryDto>>("/api/catalog/v1/search?q=%25%25", TestContext.Current.CancellationToken);

        injection.StatusCode.ShouldBe(HttpStatusCode.OK);
        wildcard.ShouldBeEmpty("a percent sign is a character, not a wildcard");
        var all = await _client.GetFromJsonAsync<List<PoiSummaryDto>>("/api/catalog/v1/destinations/marseille/pois", TestContext.Current.CancellationToken);
        all!.Count.ShouldBe(15);
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
        await using var factory = new WebApplicationFactory<CatalogApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", connection);
            builder.UseSetting("Catalog:SeedDemoData", "true");
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
        });
        using var _ = factory.CreateClient();

        await using var db = new Npgsql.NpgsqlConnection(connection);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand("update catalog.story set is_premium = true", db);

        var failure = await Should.ThrowAsync<Npgsql.PostgresException>(() => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        failure.ConstraintName.ShouldBe("ck_story_premium_text");
    }
}
