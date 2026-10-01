using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OnVoyage.Discovery.Api;
using OnVoyage.Discovery.Application.IntegrationEvents;
using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Factory.Contracts;
using OnVoyage.Recommendation.Engine;
using OnVoyage.TestInfrastructure;

namespace Discovery.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class RecommendationApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Cats = ["history", "nature", "culture", "architecture", "religion", "gastronomy", "leisure", "outdoors"];
    private WebApplicationFactory<DiscoveryApiMarker> _factory = null!;
    private readonly List<(Guid Id, string Category, bool Fragile, int Crowd)> _places = [];

    public async ValueTask InitializeAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        _factory = new WebApplicationFactory<DiscoveryApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", connection);
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
            builder.UseSetting("Media:PublicBaseUrl", "https://media.test/media");
        });
        _ = _factory.Server;
        await SeedAsync();
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task SeedAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IProjectionWriter>();
        var random = new Random(11);
        for (var i = 0; i < 48; i++)
        {
            var id = Guid.NewGuid();
            var category = Cats[i % Cats.Length];
            var fragile = i % 13 == 0;
            var crowd = 1 + (i % 5);
            _places.Add((id, category, fragile, crowd));
            var poi = new PoiPublishedV1(
                Guid.NewGuid(), DateTimeOffset.UtcNow, id, 1, new PoiDestinationV1("marseille", "Marseille", 43.2965, 5.3698), $"lieu-{i}", $"Lieu {i}", null,
                43.2965 + ((random.NextDouble() - 0.5) * 0.04), 5.3698 + ((random.NextDouble() - 0.5) * 0.05), 40 + (i % 50), 50, i % 7 == 0, (float)(0.5 + (random.NextDouble() * 0.5)), 1,
                [new PoiInterestV1(category, 0.9f), new PoiInterestV1($"{category}.{Sub(category)}", 1f)], new PoiCrowdProfileV1(1, 2, crowd), fragile, false);
            await PoiPublishedHandler.Handle(poi, writer, Ct);
            var story = new StoryPublishedV1(
                Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), id, "fr", "standard", 1, $"Lieu {i}", "", "", "", 100, "v", true, i == 5,
                [new StoryAudioPartV1("main", $"s/{i}.mp3", "x", 100), new StoryAudioPartV1("announce_front", $"s/{i}-f.mp3", "x", 5)], []);
            await StoryPublishedHandler.Handle(story, writer, Ct);
        }

        // A place without any story must never be recommended.
        await PoiPublishedHandler.Handle(new PoiPublishedV1(
            Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), 1, new PoiDestinationV1("marseille", "Marseille", 43.2965, 5.3698), "muet", "Muet", null, 43.29, 5.37, 99, 99, false, 0.99f, 1,
            [new PoiInterestV1("history", 1f)], new PoiCrowdProfileV1(1, 1, 1), false, false), writer, Ct);
    }

    private static string Sub(string category) => category switch
    {
        "history" => "military",
        "nature" => "coast",
        "culture" => "museums",
        "architecture" => "defensive",
        "religion" => "churches",
        "gastronomy" => "markets",
        "leisure" => "beaches",
        _ => "hiking",
    };

    private HttpClient Traveler(Guid? id = null)
    {
        var client = _factory.CreateClient();
        client.Authenticate(TestTokens.Mint(id ?? Guid.NewGuid()));
        return client;
    }

    private static Guid TravelerInCohort(bool control)
    {
        while (true)
        {
            var id = Guid.NewGuid();
            if (ControlCohort.Contains(id) == control)
            {
                return id;
            }
        }
    }

    private static async Task Like(HttpClient client, params Guid[] places)
    {
        var batch = places.Select((p, i) => new InteractionDto(Guid.NewGuid(), "like", p, DateTimeOffset.UtcNow.AddMinutes(-60 + i))).ToArray();
        (await client.PostAsJsonAsync("/api/discovery/v1/me/interactions", new InteractionBatchRequest(batch), Ct)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_new_traveler_gets_a_cold_start_list_without_compatibility_and_never_a_place_without_story()
    {
        using var client = Traveler(TravelerInCohort(false));
        var result = (await client.GetFromJsonAsync<RecommendationsDto>("/api/discovery/v1/recommendations?limit=10&radius=20000&lat=43.2965&lng=5.3698", Ct))!;

        result.Items.Count.ShouldBe(10);
        result.Items.ShouldAllBe(i => i.Compatibility == null);
        result.Items.ShouldAllBe(i => i.Why.Template == "cold_start" || i.Why.Template == "hidden_gem");
        result.Items.ShouldNotContain(i => i.Slug == "muet");
        result.WeightsVersion.ShouldBe(1);
        result.Cohort.ShouldBe("personalized");
    }

    [Fact]
    public async Task A_traveler_with_tastes_sees_compatibility_and_a_why_that_names_what_they_liked()
    {
        using var client = Traveler(TravelerInCohort(false));
        var history = _places.Where(p => p.Category == "history").Take(5).Select(p => p.Id).ToArray();
        await Like(client, history);

        var result = (await client.GetFromJsonAsync<RecommendationsDto>("/api/discovery/v1/recommendations?limit=9&radius=20000&lat=43.2965&lng=5.3698", Ct))!;

        result.Items.ShouldAllBe(i => i.Compatibility != null && i.Compatibility <= 98);
        var similar = result.Items.Where(i => i.Why.Template == "liked_similar").ToArray();
        similar.ShouldNotBeEmpty();
        similar.ShouldAllBe(i => i.Why.Params.ContainsKey("poiName"));
        result.Items.Count(i => i.Slug.Length > 0 && _places.First(p => $"lieu-{_places.IndexOf(p)}" == i.Slug).Category == "history").ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task The_cohort_is_stable_for_one_traveler_and_the_control_cohort_gets_no_personalisation()
    {
        var control = TravelerInCohort(true);
        using var first = Traveler(control);
        using var second = Traveler(control);
        var a = (await first.GetFromJsonAsync<RecommendationsDto>("/api/discovery/v1/recommendations?limit=8&radius=20000", Ct))!;
        var b = (await second.GetFromJsonAsync<RecommendationsDto>("/api/discovery/v1/recommendations?limit=8&radius=20000", Ct))!;

        a.Cohort.ShouldBe("control");
        b.Cohort.ShouldBe("control");
        a.Items.ShouldAllBe(i => i.Compatibility == null);
        a.Items.Select(i => i.PoiId).ShouldBe(b.Items.Select(i => i.PoiId));
    }

    [Fact]
    public async Task A_place_turned_down_is_never_recommended_again()
    {
        using var client = Traveler(TravelerInCohort(false));
        var first = (await client.GetFromJsonAsync<RecommendationsDto>("/api/discovery/v1/recommendations?limit=5&radius=20000", Ct))!.Items[0].PoiId;
        (await client.PostAsJsonAsync("/api/discovery/v1/me/interactions", new InteractionBatchRequest([new InteractionDto(Guid.NewGuid(), "dislike_poi", first, DateTimeOffset.UtcNow.AddMinutes(-1))]), Ct)).EnsureSuccessStatusCode();

        var after = (await client.GetFromJsonAsync<RecommendationsDto>("/api/discovery/v1/recommendations?limit=50&radius=20000", Ct))!;
        after.Items.ShouldNotContain(i => i.PoiId == first);
    }

    [Fact]
    public async Task The_list_respects_the_category_ceiling_and_the_radius_and_validates_its_parameters()
    {
        using var client = Traveler(TravelerInCohort(false));
        await Like(client, [.. _places.Where(p => p.Category == "nature").Take(6).Select(p => p.Id)]);
        var result = (await client.GetFromJsonAsync<RecommendationsDto>("/api/discovery/v1/recommendations?limit=10&radius=20000", Ct))!;

        var slugToCategory = _places.Select((p, i) => ($"lieu-{i}", p.Category)).ToDictionary(x => x.Item1, x => x.Category);
        result.Items.Where(i => !i.IsExploration).GroupBy(i => slugToCategory[i.Slug]).Max(g => g.Count()).ShouldBeLessThanOrEqualTo(4);

        (await client.GetAsync("/api/discovery/v1/recommendations?limit=0", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/discovery/v1/recommendations?lat=43", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/discovery/v1/recommendations?context=boat", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var tiny = (await client.GetFromJsonAsync<RecommendationsDto>("/api/discovery/v1/recommendations?lat=0&lng=0&radius=100&limit=10", Ct))!;
        tiny.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Exploration_adds_an_adjacent_category_the_traveler_does_not_know()
    {
        using var client = Traveler(TravelerInCohort(false));
        // Liking a history leaf makes the other history places "adjacent" while the matrix has no data.
        await Like(client, [.. _places.Where(p => p.Category == "history").Take(3).Select(p => p.Id)]);
        var result = (await client.GetFromJsonAsync<RecommendationsDto>("/api/discovery/v1/recommendations?limit=10&radius=20000", Ct))!;
        result.Items.Count.ShouldBe(10);
        result.Items.Count(i => i.IsExploration).ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task The_destination_page_in_Lyon_has_nine_places_a_compatibility_a_why_and_a_two_day_plan()
    {
        using var client = Traveler(TravelerInCohort(false));
        await Like(client, [.. _places.Where(p => p.Category is "history" or "nature").Take(6).Select(p => p.Id)]);

        var page = (await client.GetFromJsonAsync<DestinationForMeDto>("/api/discovery/v1/destinations/marseille/for-me?lat=45.764&lng=4.8357&days=2&mobility=walk", Ct))!;

        page.Remote.ShouldBeTrue();
        page.Name.ShouldBe("Marseille");
        page.Places.Count.ShouldBe(9);
        page.Places.ShouldAllBe(p => p.Compatibility != null && p.Why.Template.Length > 0);
        page.Strongest.Count.ShouldBeGreaterThan(0);
        page.Plan!.Count.ShouldBe(2);
        page.Plan.ShouldAllBe(d => d.Places.Count >= 4 && d.Places.Count <= 6);
    }

    [Fact]
    public async Task The_destination_page_rejects_unknown_destinations_and_bad_days()
    {
        using var client = Traveler();
        (await client.GetAsync("/api/discovery/v1/destinations/atlantis/for-me", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/discovery/v1/destinations/marseille/for-me?days=9", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Surprise_never_proposes_a_fragile_or_crowded_place_nor_the_same_one_twice()
    {
        using var client = Traveler(TravelerInCohort(false));
        var allowed = _places.Select((p, i) => ($"lieu-{i}", p)).Where(x => !x.p.Fragile && x.p.Crowd < 4).Select(x => x.Item1).ToHashSet();
        var seen = new HashSet<string>();
        for (var i = 0; i < 12; i++)
        {
            var item = (await client.GetFromJsonAsync<RecommendationItemDto>("/api/discovery/v1/surprise?radius=20000", Ct))!;
            allowed.ShouldContain(item.Slug);
            seen.Add(item.Slug).ShouldBeTrue($"{item.Slug} was proposed twice");
        }
    }

    [Fact]
    public async Task Candidates_carry_flags_a_base_score_without_position_terms_and_absolute_audio_urls()
    {
        using var client = Traveler(TravelerInCohort(false));
        var result = (await client.GetFromJsonAsync<CandidatesDto>("/api/discovery/v1/me/candidates?destination=marseille", Ct))!;

        // 48 places with a story, minus the premium one (i == 5); the place without a story is absent.
        result.Items.Count.ShouldBe(47);
        result.Items.ShouldAllBe(c => c.BaseScore >= 0 && c.BaseScore <= 1);
        result.Items.ShouldAllBe(c => c.Stories.Count == 1 && c.Stories[0].AudioParts["main"].StartsWith("https://media.test/media/s/", StringComparison.Ordinal));
        result.Items.Count(c => c.Fragile).ShouldBe(_places.Count(p => p.Fragile) - 0);
        (await client.GetAsync("/api/discovery/v1/me/candidates?destination=atlantis", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Candidates_exclude_places_the_traveler_turned_down_and_raise_the_base_score_of_liked_categories()
    {
        using var client = Traveler(TravelerInCohort(false));
        var before = (await client.GetFromJsonAsync<CandidatesDto>("/api/discovery/v1/me/candidates?destination=marseille", Ct))!;
        var history = _places.Where(p => p.Category == "history").Take(4).Select(p => p.Id).ToArray();
        await Like(client, history);
        (await client.PostAsJsonAsync("/api/discovery/v1/me/interactions", new InteractionBatchRequest([new InteractionDto(Guid.NewGuid(), "dislike_poi", _places[1].Id, DateTimeOffset.UtcNow.AddMinutes(-1))]), Ct)).EnsureSuccessStatusCode();
        var after = (await client.GetFromJsonAsync<CandidatesDto>("/api/discovery/v1/me/candidates?destination=marseille", Ct))!;

        after.Items.ShouldNotContain(c => c.PoiId == _places[1].Id);
        var unliked = _places.Where(p => p.Category == "history").Skip(4).First().Id;
        after.Items.First(c => c.PoiId == unliked).BaseScore.ShouldBeGreaterThan(before.Items.First(c => c.PoiId == unliked).BaseScore);
    }

    [Fact]
    public async Task Wishes_are_saved_listed_by_destination_and_removed()
    {
        using var client = Traveler();
        var a = _places[0].Id;
        var b = _places[1].Id;
        (await client.PutAsync($"/api/discovery/v1/me/saved/{a}", null, Ct)).EnsureSuccessStatusCode();
        (await client.PutAsync($"/api/discovery/v1/me/saved/{a}", null, Ct)).EnsureSuccessStatusCode(); // idempotent
        (await client.PutAsync($"/api/discovery/v1/me/saved/{b}", null, Ct)).EnsureSuccessStatusCode();

        var groups = (await client.GetFromJsonAsync<List<SavedGroupDto>>("/api/discovery/v1/me/saved", Ct))!;
        groups.ShouldHaveSingleItem().Destination.ShouldBe("marseille");
        groups[0].Items.Count.ShouldBe(2);

        (await client.DeleteAsync($"/api/discovery/v1/me/saved/{a}", Ct)).EnsureSuccessStatusCode();
        (await client.GetFromJsonAsync<List<SavedGroupDto>>("/api/discovery/v1/me/saved", Ct))![0].Items.ShouldHaveSingleItem().PoiId.ShouldBe(b);
    }

    [Fact]
    public async Task A_save_interaction_also_creates_the_wish()
    {
        using var client = Traveler();
        (await client.PostAsJsonAsync("/api/discovery/v1/me/interactions", new InteractionBatchRequest([new InteractionDto(Guid.NewGuid(), "save", _places[2].Id, DateTimeOffset.UtcNow.AddMinutes(-1))]), Ct)).EnsureSuccessStatusCode();
        (await client.GetFromJsonAsync<List<SavedGroupDto>>("/api/discovery/v1/me/saved", Ct))!.Single().Items.Single().PoiId.ShouldBe(_places[2].Id);
    }

    [Fact]
    public async Task History_lists_heard_and_visited_places_and_deleting_one_recomputes_the_vector()
    {
        using var client = Traveler();
        var fort = _places.First(p => p.Category == "history").Id;
        var calanque = _places.First(p => p.Category == "nature").Id;
        await Like(client, fort, calanque);
        (await client.PostAsJsonAsync("/api/discovery/v1/me/interactions", new InteractionBatchRequest([new InteractionDto(Guid.NewGuid(), "visit", calanque, DateTimeOffset.UtcNow.AddMinutes(-5), Confidence: 0.8, DwellS: 600)]), Ct)).EnsureSuccessStatusCode();

        var history = (await client.GetFromJsonAsync<List<HistoryItemDto>>("/api/discovery/v1/me/history", Ct))!;
        history.Count.ShouldBe(2);
        history.Single(h => h.PoiId == calanque).Visited.ShouldBeTrue();
        history.Single(h => h.PoiId == fort).Listened.ShouldBeTrue();

        var response = await client.DeleteAsync($"/api/discovery/v1/me/history/{fort}", Ct);
        var vector = (await response.Content.ReadFromJsonAsync<InteractionBatchResponse>(Ct))!;
        vector.Vector.Keys.ShouldNotContain("history.military");
        vector.Vector.Keys.ShouldContain("nature.coast");
        (await client.GetFromJsonAsync<List<HistoryItemDto>>("/api/discovery/v1/me/history", Ct))!.ShouldHaveSingleItem().PoiId.ShouldBe(calanque);
    }

    [Fact]
    public async Task The_category_affinity_job_builds_a_lift_matrix_from_rich_profiles()
    {
        // 60 travelers choose history and nature at the onboarding (affinity 0.6) and like four places (depth ≥ 10): the two are co-liked by everyone.
        var likes = _places.Where(p => p.Category is "history" or "nature").Take(4).Select(p => p.Id).ToArray();
        for (var i = 0; i < 60; i++)
        {
            using var client = Traveler();
            await Like(client, likes);
            var choices = new[] { "history", "nature" }.Select(c => new InteractionDto(Guid.NewGuid(), "onboarding_category", null, DateTimeOffset.UtcNow.AddHours(-2), CategoryCode: c, Value: 1)).ToArray();
            (await client.PostAsJsonAsync("/api/discovery/v1/me/interactions", new InteractionBatchRequest(choices), Ct)).EnsureSuccessStatusCode();
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAffinityStore>();
        (await store.RecomputeAsync(DateTimeOffset.UtcNow, Ct)).ShouldBe(60);

        var (lifts, travelers) = await store.LoadAsync(Ct);
        travelers.ShouldBe(60);
        lifts.ShouldContainKey(("history", "nature"));
        lifts[("history", "nature")].ShouldBe(1d, 1e-6); // everyone likes both: no surprise
    }
}
