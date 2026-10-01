using NSubstitute;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Home;
using OnVoyage.App.Core.Profile;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.App.Core.Tests;

public sealed class AppCoreTests
{
    private static PoiSummaryDto Poi(string name, double importance, bool gem, params (string Code, double W)[] weights) =>
        new(Guid.CreateVersion7(), name.ToLowerInvariant(), name, "history", 43.3, 5.4, importance, 0.8, gem ? 1 : 4, gem, null, 90,
            weights.ToDictionary(w => w.Code, w => w.W));

    [Fact]
    public void Onboarding_seeds_liked_and_disliked_level_one_categories()
    {
        var profile = ProfileUpdater.ApplyOnboarding(new LocalProfile(), ["history", "nature"], ["culture", "not-a-category"]);

        profile.Affinities["history"].ShouldBe(0.6);
        profile.Affinities["culture"].ShouldBe(-0.6);
        profile.Affinities.ContainsKey("not-a-category").ShouldBeFalse();
        profile.Depth.ShouldBe(4);
        profile.OnboardingDone.ShouldBeTrue();
    }

    [Fact]
    public void Signals_move_affinity_toward_the_signal_and_stay_bounded()
    {
        var profile = new LocalProfile();
        for (var i = 0; i < 200; i++)
        {
            profile = ProfileUpdater.ApplySignal(profile, new Dictionary<string, double> { ["history"] = 1d }, 1d);
        }

        profile.Affinities["history"].ShouldBeInRange(0.99, 1d);
        ProfileUpdater.ApplySignal(profile, new Dictionary<string, double> { ["history"] = 1d }, -0.6).Affinities["history"].ShouldBeLessThan(profile.Affinities["history"]);
    }

    [Fact]
    public void Saving_twice_removes_the_place_and_only_the_first_save_learns()
    {
        var id = Guid.CreateVersion7();
        var weights = new Dictionary<string, double> { ["nature"] = 1d };

        var saved = ProfileUpdater.ToggleSaved(new LocalProfile(), id, weights);
        var unsaved = ProfileUpdater.ToggleSaved(saved, id, weights);

        saved.Saved.ShouldContain(id);
        saved.Depth.ShouldBe(1);
        unsaved.Saved.ShouldBeEmpty();
        unsaved.Depth.ShouldBe(1);
    }

    [Fact]
    public async Task Home_feed_ranks_by_taste_and_explains_with_a_template()
    {
        var history = Poi("Fort", 0.5, false, ("history", 1d));
        var nature = Poi("Calanque", 0.9, false, ("nature", 1d));
        var client = Substitute.For<ICatalogClient>();
        client.GetDestinationAsync("marseille", Arg.Any<CancellationToken>()).Returns(new DestinationDto("marseille", "Marseille", 43.3, 5.4, 2));
        client.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([nature, history]);
        var store = new InMemoryProfileStore();
        // A traveler id outside the 20 % control cohort is required for personalisation.
        var profile = ProfileUpdater.ApplyOnboarding(FindNonControlProfile(), ["history"], ["nature"]) with { Depth = 8 };
        await store.SaveAsync(profile, CancellationToken.None);

        var feed = await new HomeFeedService(client, store, SessionFor(profile)).BuildAsync(null, null, CancellationToken.None);

        feed.IsControl.ShouldBeFalse();
        feed.ForYouTitle.ShouldBe("Pour vous");
        feed.ForYou[0].Poi.Name.ShouldBe("Fort");
        feed.ForYou[0].Why.ShouldBe("Vous aimez : l'histoire.");
        feed.ForYou[0].CompatibilityPercent.ShouldNotBeNull();
    }

    [Fact]
    public async Task Control_cohort_sees_incontournables_without_compatibility()
    {
        var client = Substitute.For<ICatalogClient>();
        client.GetDestinationAsync("marseille", Arg.Any<CancellationToken>()).Returns(new DestinationDto("marseille", "Marseille", 43.3, 5.4, 2));
        client.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([Poi("Fort", 0.5, false, ("history", 1d)), Poi("Calanque", 0.9, false, ("nature", 1d))]);
        var store = new InMemoryProfileStore();
        var controlProfile = FindControlProfile();
        await store.SaveAsync(controlProfile, CancellationToken.None);

        var feed = await new HomeFeedService(client, store, SessionFor(controlProfile)).BuildAsync(null, null, CancellationToken.None);

        feed.IsControl.ShouldBeTrue();
        feed.ForYouTitle.ShouldBe("Incontournables");
        feed.ForYou[0].Poi.Name.ShouldBe("Calanque");
        feed.ForYou.ShouldAllBe(card => card.CompatibilityPercent == null && card.Badge == "Populaire");
    }

    private static ISessionProvider SessionFor(LocalProfile profile)
    {
        var sessions = Substitute.For<ISessionProvider>();
        sessions.EnsureSessionAsync(Arg.Any<CancellationToken>()).Returns(new AuthSessionDto("t", DateTimeOffset.MaxValue, "r", DateTimeOffset.MaxValue, profile.TravelerId, true, null, []));
        return sessions;
    }

    [Fact]
    public async Task The_profile_adopts_the_account_id_so_the_cohort_follows_the_account()
    {
        var client = Substitute.For<ICatalogClient>();
        client.GetDestinationAsync("marseille", Arg.Any<CancellationToken>()).Returns(new DestinationDto("marseille", "Marseille", 43.3, 5.4, 0));
        client.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([]);
        var store = new InMemoryProfileStore();
        var accountId = Guid.CreateVersion7();
        var session = Substitute.For<ISessionProvider>();
        session.EnsureSessionAsync(Arg.Any<CancellationToken>()).Returns(new AuthSessionDto("t", DateTimeOffset.MaxValue, "r", DateTimeOffset.MaxValue, accountId, false, "a@b.org", []));

        await new HomeFeedService(client, store, session).BuildAsync(null, null, CancellationToken.None);

        (await store.LoadAsync(CancellationToken.None)).TravelerId.ShouldBe(accountId);
    }

    private static LocalProfile FindNonControlProfile() => Find(false);

    private static LocalProfile FindControlProfile() => Find(true);

    private static LocalProfile Find(bool control)
    {
        while (true)
        {
            var profile = new LocalProfile();
            if (OnVoyage.Recommendation.Engine.ControlCohort.Contains(profile.TravelerId) == control)
            {
                return profile;
            }
        }
    }
}
