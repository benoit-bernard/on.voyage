using NSubstitute;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Planning;
using OnVoyage.App.Core.Profile;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Platform.Contracts;
using OnVoyage.Recommendation.Engine;
using TravelMode = OnVoyage.Recommendation.Engine.TravelMode;

namespace OnVoyage.App.Core.Tests;

public sealed class DestinationServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Categories = ["history", "nature", "culture", "architecture", "religion", "gastronomy", "leisure", "outdoors"];

    private static List<PoiSummaryDto> City(int count = 40)
    {
        var random = new Random(3);
        return [.. Enumerable.Range(0, count).Select(i =>
        {
            var category = Categories[i % Categories.Length];
            return new PoiSummaryDto(
                Guid.NewGuid(), $"p{i}", $"Lieu {i}", category, 43.2965 + ((random.NextDouble() - 0.5) * 0.04), 5.3698 + ((random.NextDouble() - 0.5) * 0.05),
                0.4 + (random.NextDouble() * 0.5), 0.5 + (random.NextDouble() * 0.4), 1 + (i % 4), i % 7 == 0, null, 90,
                new Dictionary<string, double> { [category] = 1d });
        })];
    }

    private static (DestinationService Service, LocalProfile Profile) Build(List<PoiSummaryDto> pois, Func<LocalProfile, LocalProfile>? profile = null)
    {
        var catalog = Substitute.For<ICatalogClient>();
        catalog.GetDestinationAsync("marseille", Arg.Any<CancellationToken>()).Returns(new DestinationDto("marseille", "Marseille", 43.2965, 5.3698, pois.Count));
        catalog.GetPoisAsync("marseille", Arg.Any<double?>(), Arg.Any<double?>(), Arg.Any<CancellationToken>()).Returns(pois);

        var start = new LocalProfile();
        while (ControlCohort.Contains(start.TravelerId))
        {
            start = new LocalProfile();
        }

        var shaped = (profile ?? (p => p))(start);
        var store = new InMemoryProfileStore();
        store.SaveAsync(shaped, CancellationToken.None).GetAwaiter().GetResult();
        var sessions = Substitute.For<ISessionProvider>();
        sessions.EnsureSessionAsync(Arg.Any<CancellationToken>()).Returns(new AuthSessionDto("t", DateTimeOffset.MaxValue, "r", DateTimeOffset.MaxValue, shaped.TravelerId, true, null, []));
        return (new DestinationService(catalog, store, sessions), shaped);
    }

    private static LocalProfile Tasted(LocalProfile p) => p with
    {
        Affinities = new() { ["history"] = 0.9, ["nature"] = 0.7, ["culture"] = 0.5, ["gastronomy"] = 0.3, ["leisure"] = 0.1, ["religion"] = -0.8, ["outdoors"] = -0.4, ["architecture"] = -0.2 },
        Depth = 20,
    };

    [Fact]
    public async Task A_traveler_in_Lyon_sees_nine_places_with_compatibility_and_a_why()
    {
        var (service, _) = Build(City(), Tasted);

        // Lyon is ~280 km from Marseille: outside the destination.
        var view = await service.BuildAsync(45.764, 4.8357, Ct);

        view.Remote.ShouldBeTrue();
        view.Places.Count.ShouldBe(9);
        view.Places.ShouldAllBe(p => p.Card.CompatibilityPercent != null && p.Card.CompatibilityPercent <= 98);
        view.Places.ShouldAllBe(p => p.Card.Why.StartsWith("Vous aimez", StringComparison.Ordinal));
        view.Places.ShouldAllBe(p => p.FromCenter && p.DistanceMeters < 10_000);
    }

    [Fact]
    public async Task The_nine_places_respect_the_category_ceiling()
    {
        var (service, _) = Build(City(), p => Tasted(p) with { Affinities = new() { ["history"] = 1d } });
        var view = await service.BuildAsync(null, null, Ct);

        view.Places.GroupBy(p => p.Card.Poi.Category).Max(g => g.Count()).ShouldBeLessThanOrEqualTo((int)Math.Ceiling(0.4 * 9));
    }

    [Fact]
    public async Task The_profile_summary_shows_the_five_strongest_and_the_two_weakest_affinities()
    {
        var (service, _) = Build(City(), Tasted);
        var view = await service.BuildAsync(null, null, Ct);

        view.Strongest.Select(b => b.Code).ShouldBe(["history", "nature", "culture", "gastronomy", "leisure"]);
        view.Weakest.Select(b => b.Code).ShouldBe(["religion", "outdoors"]);
    }

    [Fact]
    public async Task A_place_the_traveler_turned_down_never_appears()
    {
        var pois = City();
        var banned = pois[0];
        var (service, _) = Build(pois, p => Tasted(p) with { Excluded = [banned.Id] });
        var view = await service.BuildAsync(null, null, Ct);
        view.Places.ShouldNotContain(p => p.Card.Poi.Id == banned.Id);

        (await service.PlanAsync(2, TravelMode.Walk, null, null, Ct)).SelectMany(d => d.Places).ShouldNotContain(p => p.Card.Poi.Id == banned.Id);
    }

    [Fact]
    public async Task Two_days_on_foot_give_two_days_of_four_to_six_places_with_leg_distances()
    {
        var (service, _) = Build(City(), Tasted);
        var plan = await service.PlanAsync(2, TravelMode.Walk, null, null, Ct);

        plan.Count.ShouldBe(2);
        plan.ShouldAllBe(d => d.Places.Count >= 4 && d.Places.Count <= 6);
        plan.ShouldAllBe(d => d.Places[0].LegMeters == 0 && Math.Abs(d.TotalMeters - d.Places.Sum(p => p.LegMeters)) <= d.Places.Count); // legs are rounded one by one
    }

    [Fact]
    public async Task The_plan_is_stable_for_the_same_traveler()
    {
        var (service, _) = Build(City(), Tasted);
        var first = await service.PlanAsync(3, TravelMode.Bike, null, null, Ct);
        var second = await service.PlanAsync(3, TravelMode.Bike, null, null, Ct);
        second.Select(d => d.Places.Select(p => p.Card.Poi.Id)).ShouldBe(first.Select(d => d.Places.Select(p => p.Card.Poi.Id)));
    }

    [Fact]
    public async Task Without_known_tastes_the_page_still_lists_places_with_a_popular_or_gem_badge()
    {
        var (service, _) = Build(City());
        var view = await service.BuildAsync(null, null, Ct);

        view.Strongest.ShouldBeEmpty();
        view.Places.Count.ShouldBe(9);
        view.Places.ShouldAllBe(p => p.Card.CompatibilityPercent == null && (p.Card.Badge == "Populaire" || p.Card.Badge == "Pépite"));
    }
}
