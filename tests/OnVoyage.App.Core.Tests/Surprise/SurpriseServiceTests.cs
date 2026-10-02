#pragma warning disable xUnit1051 // driven against in-memory fakes
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Interactions;
using OnVoyage.App.Core.Surprise;
using OnVoyage.App.Core.Tests.Audio;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Discovery.Contracts;

namespace OnVoyage.App.Core.Tests.Surprise;

public sealed class SurpriseServiceTests
{
    private readonly IDiscoveryClient _discovery = Substitute.For<IDiscoveryClient>();
    private readonly ICatalogClient _catalog = Substitute.For<ICatalogClient>();
    private readonly RecordingAnalytics _analytics = new();
    private readonly SurpriseService _service;

    public SurpriseServiceTests() => _service = new SurpriseService(_discovery, _catalog, _analytics);

    private static RecommendationItemDto Item(string name, WhyDto why, bool exploration) =>
        new(Guid.NewGuid(), name.ToLowerInvariant().Replace(' ', '-'), name, 0.7, 72, why, exploration, null);

    private static PoiDetailDto Detail(RecommendationItemDto item, string category, double latitude, double longitude) =>
        new(item.PoiId, item.Slug, item.Name, category, latitude, longitude, 0.6, 2, true, [], []);

    [Fact]
    public async Task An_exploration_pick_says_it_leaves_the_habits_gives_the_reason_and_the_distance()
    {
        var item = Item("Calanque de Sormiou", new WhyDto("categories", new Dictionary<string, string> { ["categories"] = "history,nature" }), exploration: true);
        _discovery.GetSurpriseAsync(43.2965, 5.37, SurpriseService.RadiusMeters, Arg.Any<CancellationToken>()).Returns(item);
        _catalog.GetPoiAsync(item.Slug, Arg.Any<CancellationToken>()).Returns(Detail(item, "nature", 43.2965 + (4_000 / 111_320d), 5.37));

        var outcome = await _service.DrawAsync(new Position(43.2965, 5.37), CancellationToken.None);

        outcome.Status.ShouldBe(SurpriseStatus.Found);
        var view = outcome.Surprise!;
        view.Name.ShouldBe("Calanque de Sormiou");
        view.IsExploration.ShouldBeTrue();
        ((double)view.DistanceMeters!.Value).ShouldBe(4_000d, 10d);
        view.Explanation.ShouldBe("Vous n'avez pas encore beaucoup exploré la nature. Vous aimez : l'histoire et la nature. À 4 km de vous.");
    }

    [Theory]
    [InlineData("liked_similar", "poiName", "Fort Saint-Nicolas", "Vous avez aimé Fort Saint-Nicolas : ce lieu lui ressemble.")]
    [InlineData("creator_followed", "creator", "marie", "Recommandé par @marie, que vous suivez.")]
    [InlineData("creator_similar", "creator", "marie", "Adoré par @marie, créateur proche de vos goûts.")]
    [InlineData("cold_start", "destination", "Marseille", "Un incontournable de Marseille.")]
    [InlineData("hidden_gem", "x", "y", "Moins fréquenté, tout aussi riche.")]
    [InlineData("unknown_template", "x", "y", "Un lieu à découvrir.")]
    public void Each_template_of_the_server_becomes_a_french_sentence(string template, string key, string value, string expected)
    {
        var item = Item("Lieu", new WhyDto(template, new Dictionary<string, string> { [key] = value }), exploration: false);

        SurpriseService.Explain(item, "history", null).ShouldBe(expected);
    }

    [Fact]
    public async Task Without_a_position_nothing_is_sent_and_no_distance_is_shown()
    {
        var item = Item("Lieu", new WhyDto("hidden_gem", new Dictionary<string, string>()), exploration: false);
        _discovery.GetSurpriseAsync(null, null, SurpriseService.RadiusMeters, Arg.Any<CancellationToken>()).Returns(item);
        _catalog.GetPoiAsync(item.Slug, Arg.Any<CancellationToken>()).Returns(Detail(item, "history", 43.3, 5.4));

        var outcome = await _service.DrawAsync(null, CancellationToken.None);

        outcome.Surprise!.DistanceMeters.ShouldBeNull();
        outcome.Surprise.Explanation.ShouldBe("Moins fréquenté, tout aussi riche.");
    }

    [Fact]
    public async Task When_nothing_is_left_the_outcome_says_so_and_the_event_counts_zero_results()
    {
        _discovery.GetSurpriseAsync(Arg.Any<double?>(), Arg.Any<double?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((RecommendationItemDto?)null);

        (await _service.DrawAsync(null, CancellationToken.None)).Status.ShouldBe(SurpriseStatus.NothingLeft);

        var tracked = _analytics.Events.Single();
        tracked.Name.ShouldBe("surprise_requested");
        tracked.Properties.ShouldBe(new Dictionary<string, object?> { ["results_count"] = 0 });
    }

    [Fact]
    public async Task A_network_failure_is_reported_without_an_event()
    {
        _discovery.GetSurpriseAsync(Arg.Any<double?>(), Arg.Any<double?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException());

        (await _service.DrawAsync(null, CancellationToken.None)).Status.ShouldBe(SurpriseStatus.Unavailable);

        _analytics.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failing_catalog_does_not_lose_the_surprise()
    {
        var item = Item("Lieu", new WhyDto("hidden_gem", new Dictionary<string, string>()), exploration: true);
        _discovery.GetSurpriseAsync(Arg.Any<double?>(), Arg.Any<double?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(item);
        _catalog.GetPoiAsync(item.Slug, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException());

        var outcome = await _service.DrawAsync(new Position(43.3, 5.4), CancellationToken.None);

        outcome.Status.ShouldBe(SurpriseStatus.Found);
        outcome.Surprise!.Explanation.ShouldBe("Moins fréquenté, tout aussi riche.", "no category, no distance: only the reason");
    }

    [Fact]
    public async Task Twenty_successive_draws_show_at_least_eight_different_places_when_the_server_moves_on()
    {
        // The server remembers the last twenty proposals (§6.10); the client must not loop on one result by itself.
        var items = Enumerable.Range(0, 12).Select(i => Item($"Lieu {i}", new WhyDto("hidden_gem", new Dictionary<string, string>()), exploration: i % 3 == 0)).ToArray();
        var calls = 0;
        _discovery.GetSurpriseAsync(Arg.Any<double?>(), Arg.Any<double?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ => items[calls++ % items.Length]);
        _catalog.GetPoiAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => Detail(items.First(i => i.Slug == call.Arg<string>()), "history", 43.3, 5.4));

        HashSet<Guid> seen = [];
        for (var i = 0; i < 20; i++)
        {
            seen.Add((await _service.DrawAsync(new Position(43.3, 5.4), CancellationToken.None)).Surprise!.PoiId);
        }

        seen.Count.ShouldBeGreaterThanOrEqualTo(8);
    }
}
