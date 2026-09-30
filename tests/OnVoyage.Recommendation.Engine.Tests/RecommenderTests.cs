using OnVoyage.Recommendation.Engine;

namespace OnVoyage.Recommendation.Engine.Tests;

public sealed class RecommenderTests
{
    private static Candidate Place(string id, double importance = 0.5, double? distance = 500, int crowd = 1, bool gem = false, params (string Code, double Weight)[] weights) =>
        new(id, weights.ToDictionary(w => w.Code, w => w.Weight), importance, 0.8, distance, crowd, gem);

    private static TasteProfile Profile(int depth, params (string Code, double Value)[] affinities) =>
        new(affinities.ToDictionary(a => a.Code, a => a.Value), depth);

    [Fact]
    public void Interested_traveler_gets_matching_place_first()
    {
        var profile = Profile(8, ("history", 1d), ("nature", -1d));
        var history = Place("history-place", weights: [("history", 1d)]);
        var nature = Place("nature-place", weights: [("nature", 1d)]);

        var ranked = Recommender.Rank(profile, [nature, history], TravelMode.Walk);

        ranked[0].Candidate.Id.ShouldBe("history-place");
    }

    [Fact]
    public void Cold_start_never_returns_an_empty_list_and_ignores_interest()
    {
        var profile = TasteProfile.Empty;
        var important = Place("important", importance: 0.95, distance: 300, weights: [("history", 1d)]);
        var minor = Place("minor", importance: 0.2, distance: 300, weights: [("nature", 1d)]);

        var ranked = Recommender.Rank(profile, [minor, important], TravelMode.Walk);

        ranked.Count.ShouldBe(2);
        ranked[0].Candidate.Id.ShouldBe("important");
        ranked[0].CompatibilityPercent.ShouldBeNull();
        ranked[0].Reason.Code.ShouldBe(ReasonCode.ColdStart);
    }

    [Fact]
    public void Effective_weights_keep_the_total_constant_during_cold_start()
    {
        var options = new RecommendationOptions();
        var total = options.Interest + options.Collaborative + options.Importance + options.Quality;

        foreach (var depth in new[] { 0, 2, 5, 12 })
        {
            var (interest, importance, quality) = Recommender.EffectiveWeights(Profile(depth), options);
            (interest + importance + quality).ShouldBe(total, 1e-9);
        }
    }

    [Fact]
    public void Compatibility_is_capped_and_only_shown_from_depth_five()
    {
        var perfect = Place("perfect", weights: [("history", 1d)]);

        var shallow = Recommender.Rank(Profile(4, ("history", 1d)), [perfect], TravelMode.Walk)[0];
        var deep = Recommender.Rank(Profile(5, ("history", 1d)), [perfect], TravelMode.Walk)[0];

        shallow.CompatibilityPercent.ShouldBeNull();
        deep.CompatibilityPercent.ShouldBe(98);
    }

    [Fact]
    public void Reason_names_the_two_strongest_categories()
    {
        var profile = Profile(6, ("history", 0.9), ("architecture", 0.6), ("nature", 0.1));
        var place = Place("p", weights: [("history", 1d), ("architecture", 1d), ("nature", 1d)]);

        var reason = Recommender.Rank(profile, [place], TravelMode.Walk)[0].Reason;

        reason.Code.ShouldBe(ReasonCode.Categories);
        reason.Categories.ShouldBe(["history", "architecture"]);
    }

    [Fact]
    public void Balanced_ethics_prefers_a_quiet_gem_over_an_equal_crowded_place()
    {
        var crowded = Place("crowded", crowd: 5, weights: [("history", 1d)]);
        var gem = Place("gem", crowd: 1, gem: true, weights: [("history", 1d)]);

        var ranked = Recommender.Rank(Profile(8, ("history", 1d)), [crowded, gem], TravelMode.Walk);

        ranked[0].Candidate.Id.ShouldBe("gem");
    }

    [Fact]
    public void Disabled_ethics_ignores_crowding()
    {
        var crowded = Place("a-crowded", crowd: 5, weights: [("history", 1d)]);
        var gem = Place("b-gem", crowd: 1, gem: true, weights: [("history", 1d)]);

        var ranked = Recommender.Rank(Profile(8, ("history", 1d)), [gem, crowded], TravelMode.Walk, new RecommendationOptions { Ethical = EthicalLevel.Off });

        ranked[0].Score.ShouldBe(ranked[1].Score, 1e-9);
    }

    [Fact]
    public void Closer_place_scores_higher_when_everything_else_is_equal()
    {
        var near = Place("near", distance: 100, weights: [("history", 1d)]);
        var far = Place("far", distance: 5000, weights: [("history", 1d)]);

        Recommender.Rank(Profile(8, ("history", 1d)), [far, near], TravelMode.Walk)[0].Candidate.Id.ShouldBe("near");
    }

    [Fact]
    public void Ranking_is_deterministic_on_ties()
    {
        var a = Place("a", weights: [("history", 1d)]);
        var b = Place("b", weights: [("history", 1d)]);

        Recommender.Rank(Profile(8), [b, a], TravelMode.Walk).Select(s => s.Candidate.Id).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Control_ranking_follows_importance_and_distance_only()
    {
        var far = Place("far", importance: 0.9, distance: 15000);
        var close = Place("close", importance: 0.8, distance: 100);

        Recommender.RankControl([far, close], TravelMode.Walk).Select(c => c.Id).ShouldBe(["close", "far"]);
    }

    [Fact]
    public void Control_cohort_is_stable_and_roughly_twenty_percent()
    {
        var ids = Enumerable.Range(0, 2000).Select(i => new Guid(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1])).ToArray();

        ids.All(id => ControlCohort.Contains(id) == ControlCohort.Contains(id)).ShouldBeTrue();
        var share = ids.Count(id => ControlCohort.Contains(id)) / (double)ids.Length;
        share.ShouldBeInRange(0.15, 0.25);
    }
}
