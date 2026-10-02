using OnVoyage.Recommendation.Engine;

namespace OnVoyage.Recommendation.Engine.Tests;

/// <summary>§6.5: neighbours by cosine, the smoothed collaborative score, its adaptive weight in the score and its explanation.</summary>
public sealed class CollaborativeTests
{
    private static readonly Guid Me = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static NeighborCandidate Person(int n, params float[] vector) => new(Guid.Parse($"00000000-0000-0000-0000-{n + 100:D12}"), vector);

    private static Guid Poi(int n) => Guid.Parse($"aaaaaaaa-0000-0000-0000-{n:D12}");

    // ---- cosine and neighbours

    [Fact]
    public void Cosine_is_one_for_the_same_direction_zero_for_unrelated_and_minus_one_for_opposite()
    {
        CollaborativeFiltering.Cosine([1f, 0f], [3f, 0f]).ShouldBe(1d, 1e-9);
        CollaborativeFiltering.Cosine([1f, 0f], [0f, 5f]).ShouldBe(0d, 1e-9);
        CollaborativeFiltering.Cosine([1f, 2f], [-1f, -2f]).ShouldBe(-1d, 1e-9);
    }

    [Fact]
    public void A_traveler_without_any_signal_has_no_direction_so_no_neighbour()
    {
        CollaborativeFiltering.Cosine([0f, 0f], [1f, 1f]).ShouldBe(0d);
        CollaborativeFiltering.Nearest(Me, [0f, 0f], [Person(1, 1f, 1f)], 5).ShouldBeEmpty();
        CollaborativeFiltering.Nearest(Me, [1f, 1f], [Person(1, 0f, 0f)], 5).ShouldBeEmpty();
    }

    [Fact]
    public void Neighbours_are_the_closest_travelers_never_the_traveler_nor_someone_with_opposite_tastes()
    {
        var pool = new[]
        {
            new NeighborCandidate(Me, [1f, 0f, 0f]),
            Person(1, 1f, 0f, 0f),       // identical
            Person(2, 0.9f, 0.3f, 0f),   // close
            Person(3, 0f, 1f, 0f),       // unrelated
            Person(4, -1f, 0f, 0f),      // opposite
        };

        var neighbors = CollaborativeFiltering.Nearest(Me, [1f, 0f, 0f], pool, k: 10);

        neighbors.Select(n => n.Id).ShouldBe([Person(1, 0).Id, Person(2, 0).Id]);
        neighbors[0].Similarity.ShouldBe(1d, 1e-9);
    }

    [Fact]
    public void Only_the_k_closest_are_kept_and_ties_are_broken_by_identifier_so_a_rerun_is_identical()
    {
        var pool = Enumerable.Range(1, 8).Select(n => Person(n, 1f, 0f)).Reverse().ToArray();

        var first = CollaborativeFiltering.Nearest(Me, [1f, 0f], pool, k: 3);
        var second = CollaborativeFiltering.Nearest(Me, [1f, 0f], pool.Reverse(), k: 3);

        first.Select(n => n.Id).ShouldBe(second.Select(n => n.Id));
        first.Select(n => n.Id).ShouldBe([Person(1, 0).Id, Person(2, 0).Id, Person(3, 0).Id]);
    }

    // ---- the score

    private static Dictionary<Guid, IReadOnlyDictionary<Guid, double>> Ratings(params (NeighborCandidate Who, Guid Poi, double Rating)[] items) =>
        items.GroupBy(i => i.Who.Id).ToDictionary(g => g.Key, g => (IReadOnlyDictionary<Guid, double>)g.ToDictionary(i => i.Poi, i => i.Rating));

    [Fact]
    public void The_score_is_the_similarity_weighted_mean_smoothed_by_lambda()
    {
        var a = Person(1, 1f); var b = Person(2, 1f); var c = Person(3, 1f);
        var neighbors = new[] { new Neighbor(a.Id, 1d), new Neighbor(b.Id, 0.5d), new Neighbor(c.Id, 0.5d) };
        var ratings = Ratings((a, Poi(1), 1d), (b, Poi(1), 1d), (c, Poi(1), -1d));

        var score = CollaborativeFiltering.Scores(neighbors, ratings, lambda: 5d, minSupport: 3).ShouldHaveSingleItem();

        score.PoiId.ShouldBe(Poi(1));
        score.Support.ShouldBe(3);
        score.Score.ShouldBe((1d + 0.5d - 0.5d) / (1d + 0.5d + 0.5d + 5d), 1e-9);
    }

    [Fact]
    public void A_unanimous_crowd_approaches_one_but_a_few_voices_do_not()
    {
        NeighborCandidate[] crowd = [.. Enumerable.Range(1, 40).Select(n => Person(n, 1f))];
        var neighbors = crowd.Select(p => new Neighbor(p.Id, 1d)).ToArray();

        var many = CollaborativeFiltering.Scores(neighbors, Ratings([.. crowd.Select(p => (p, Poi(1), 1d))])).Single();
        var few = CollaborativeFiltering.Scores(neighbors.Take(3).ToArray(), Ratings([.. crowd.Take(3).Select(p => (p, Poi(1), 1d))])).Single();

        many.Score.ShouldBeGreaterThan(0.85d);
        few.Score.ShouldBe(3d / 8d, 1e-9);
    }

    [Fact]
    public void A_place_rated_by_fewer_than_the_minimum_number_of_neighbours_gets_no_score()
    {
        NeighborCandidate[] people = [Person(1, 1f), Person(2, 1f), Person(3, 1f)];
        var neighbors = people.Select(p => new Neighbor(p.Id, 1d)).ToArray();
        var ratings = Ratings((people[0], Poi(1), 1d), (people[1], Poi(1), 1d), (people[0], Poi(2), 1d), (people[1], Poi(2), 1d), (people[2], Poi(2), 1d));

        var scores = CollaborativeFiltering.Scores(neighbors, ratings, minSupport: 3);

        scores.ShouldHaveSingleItem().PoiId.ShouldBe(Poi(2));
    }

    [Fact]
    public void Only_the_best_scores_are_kept_best_first()
    {
        NeighborCandidate[] people = [Person(1, 1f), Person(2, 1f), Person(3, 1f)];
        var neighbors = people.Select(p => new Neighbor(p.Id, 1d)).ToArray();
        var ratings = Ratings([.. Enumerable.Range(1, 5).SelectMany(poi => people.Select(p => (p, Poi(poi), poi * 0.2d)))]);

        var scores = CollaborativeFiltering.Scores(neighbors, ratings, max: 2);

        scores.Select(s => s.PoiId).ShouldBe([Poi(5), Poi(4)]);
    }

    [Fact]
    public void A_neighbour_without_ratings_or_a_rating_out_of_range_is_harmless()
    {
        NeighborCandidate[] people = [Person(1, 1f), Person(2, 1f), Person(3, 1f), Person(4, 1f)];
        var neighbors = people.Select(p => new Neighbor(p.Id, 1d)).ToArray();
        var ratings = Ratings((people[0], Poi(1), 9d), (people[1], Poi(1), 1d), (people[2], Poi(1), 1d));

        var score = CollaborativeFiltering.Scores(neighbors, ratings).Single();

        score.Score.ShouldBe(3d / 8d, 1e-9, "a rating above 1 counts as 1");
    }

    // ---- weight in the score (§6.5, §6.7)

    private static Candidate Place(string id, CollaborativeSignal? cf = null) =>
        new(id, new Dictionary<string, double> { ["history"] = 1d }, 0.5, 0.8, 500, 1, false, 0, null, cf);

    private static TasteProfile Deep(int depth = 12) => new(new Dictionary<string, double> { ["history"] = 0.2 }, depth);

    [Fact]
    public void A_neighbour_with_the_same_profile_who_loved_a_place_raises_its_score()
    {
        var without = Recommender.Rank(Deep(), [Place("a")], TravelMode.Walk)[0].Score;
        var loved = Recommender.Rank(Deep(), [Place("a", new CollaborativeSignal(0.8, 10))], TravelMode.Walk)[0].Score;
        var disliked = Recommender.Rank(Deep(), [Place("a", new CollaborativeSignal(-0.8, 10))], TravelMode.Walk)[0].Score;

        loved.ShouldBeGreaterThan(without);
        disliked.ShouldBeLessThan(without);
    }

    [Fact]
    public void With_few_neighbours_the_weight_shrinks_in_proportion_to_the_support()
    {
        var options = new RecommendationOptions();
        double Contribution(int support) => Recommender.Rank(Deep(), [Place("a", new CollaborativeSignal(1d, support))], TravelMode.Walk, options)[0].CollaborativeContribution;

        Contribution(10).ShouldBe(options.Collaborative * 1d, 1e-9, "CF01 = 1 and the whole w_cf");
        Contribution(5).ShouldBe(options.Collaborative * 0.5d, 1e-9);
        Contribution(1).ShouldBe(options.Collaborative * 0.1d, 1e-9);
        Contribution(40).ShouldBe(options.Collaborative, 1e-9, "more than 10 neighbours do not give more than w_cf");
        Contribution(0).ShouldBe(0d);
    }

    [Fact]
    public void The_weight_not_used_goes_to_importance_and_quality_so_the_total_never_changes()
    {
        var options = new RecommendationOptions();
        var total = options.Interest + options.Collaborative + options.Importance + options.Quality;
        foreach (var support in new[] { 0, 1, 5, 10, 30 })
        {
            foreach (var depth in new[] { 0, 3, 5, 12 })
            {
                var candidate = Place("a", support == 0 ? null : new CollaborativeSignal(0.3, support));
                var (interest, cf, importance, quality) = Recommender.WeightsFor(Deep(depth), candidate, options);
                (interest + cf + importance + quality).ShouldBe(total, 1e-9, $"depth {depth}, support {support}");
            }
        }
    }

    [Fact]
    public void During_cold_start_the_neighbours_are_ignored_whatever_they_say()
    {
        var options = new RecommendationOptions();

        foreach (var depth in new[] { 0, 2, 4 })
        {
            var ranked = Recommender.Rank(Deep(depth), [Place("a", new CollaborativeSignal(1d, 50))], TravelMode.Walk, options)[0];
            ranked.CollaborativeContribution.ShouldBe(0d);
            ranked.Score.ShouldBe(Recommender.Rank(Deep(depth), [Place("a")], TravelMode.Walk, options)[0].Score, 1e-12);
        }
    }

    [Fact]
    public void Without_any_neighbour_the_ranking_is_exactly_the_one_of_the_first_release()
    {
        // Before collaborative filtering the whole w_cf went to importance and quality: nothing changes for a place nobody rated.
        var options = new RecommendationOptions();
        var profile = Deep();
        var place = Place("a");
        var (wIm, wImp, wQ) = Recommender.EffectiveWeights(profile, options);
        var expected = (wIm * ((Recommender.InterestMatch(profile, place) + 1d) / 2d)) + (wImp * 0.5) + (options.Distance * Recommender.DistanceTerm(place, TravelMode.Walk)) + (wQ * 0.8)
            + (options.Novelty * 1d) + options.Context - (options.CrowdWeight * 0d);

        Recommender.Rank(profile, [place], TravelMode.Walk, options)[0].Score.ShouldBe(expected, 1e-9);
    }

    [Fact]
    public void The_base_score_the_device_downloads_includes_the_collaborative_term()
    {
        var profile = Deep();
        var plain = Recommender.BaseScore(profile, Place("a"));
        var withCf = Recommender.BaseScore(profile, Place("a", new CollaborativeSignal(1d, 10)));

        withCf.ShouldBeGreaterThan(plain);
    }

    // ---- explanation (§6.9, third in line)

    [Fact]
    public void The_neighbours_explain_a_place_when_they_make_up_more_than_thirty_percent_of_the_score_and_no_category_does()
    {
        // The traveler has no positive affinity for what the place is about, so the category reason does not apply.
        var profile = new TasteProfile(new Dictionary<string, double> { ["nature"] = 0.8 }, 12);
        var place = new Candidate("a", new Dictionary<string, double> { ["history"] = 1d }, 0.0, 0.0, 100_000, 1, false, 0, null, new CollaborativeSignal(1d, 10));

        var scored = Recommender.Rank(profile, [place], TravelMode.Car).Single();

        scored.CollaborativeContribution.ShouldBeGreaterThan(0.3 * scored.Score);
        scored.Reason.Code.ShouldBe(ReasonCode.Collaborative);
        scored.Reason.Categories.ShouldBeEmpty();
    }

    [Fact]
    public void A_category_the_traveler_likes_comes_before_the_neighbours_in_the_explanation()
    {
        var scored = Recommender.Rank(Deep(), [Place("a", new CollaborativeSignal(1d, 10))], TravelMode.Walk).Single();

        scored.Reason.Code.ShouldBe(ReasonCode.Categories);
    }

    [Fact]
    public void A_small_share_of_the_score_is_not_worth_an_explanation()
    {
        var profile = new TasteProfile(new Dictionary<string, double> { ["nature"] = 0.8 }, 12);
        var place = new Candidate("a", new Dictionary<string, double> { ["history"] = 1d }, 0.9, 0.9, 100, 1, false, 0, null, new CollaborativeSignal(0.0, 10));

        Recommender.Rank(profile, [place], TravelMode.Walk).Single().Reason.Code.ShouldNotBe(ReasonCode.Collaborative);
    }
}
