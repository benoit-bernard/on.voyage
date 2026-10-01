using OnVoyage.Recommendation.Engine;

namespace OnVoyage.Recommendation.Engine.Tests;

public sealed class DiversificationTests
{
    private static readonly string[] Cats = ["history", "nature", "culture", "architecture", "religion", "gastronomy"];

    private static List<ScoredCandidate> Ranked(int seed, int n = 60)
    {
        var random = new Random(seed);
        return [.. Enumerable.Range(0, n).Select(i =>
        {
            var cat = Cats[random.Next(Cats.Length)];
            var weights = new Dictionary<string, double> { [cat] = 0.8 + (random.NextDouble() * 0.2), [$"{cat}.x{random.Next(3)}"] = random.NextDouble() };
            return new ScoredCandidate(new Candidate($"p{i:000}", weights, 0.5, 0.5, null), 1d - (i * 0.01), null, new Reason(ReasonCode.ColdStart, []));
        })];
    }

    [Fact]
    public void No_dominant_category_exceeds_the_ceiling_whatever_the_list()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            foreach (var n in new[] { 5, 9, 12 })
            {
                var list = Diversification.Apply(Ranked(seed), n);
                list.Count.ShouldBe(n);
                var ceiling = Math.Max(1, (int)Math.Ceiling(0.4 * n));
                list.GroupBy(c => Learning.InterestLearning.DominantCategory(c.Candidate.Weights)).Max(g => g.Count()).ShouldBeLessThanOrEqualTo(ceiling);
            }
        }
    }

    [Fact]
    public void The_result_is_deterministic_and_starts_with_the_best_score()
    {
        var a = Diversification.Apply(Ranked(1), 9);
        var b = Diversification.Apply(Ranked(1), 9);
        a.Select(c => c.Candidate.Id).ShouldBe(b.Select(c => c.Candidate.Id));
        a[0].Candidate.Id.ShouldBe("p000");
    }

    [Fact]
    public void Similar_places_are_pushed_down_in_favour_of_different_ones()
    {
        var same = new Dictionary<string, double> { ["history"] = 1, ["history.military"] = 1 };
        var other = new Dictionary<string, double> { ["nature"] = 1, ["nature.coast"] = 1 };
        var list = new List<ScoredCandidate>
        {
            new(new Candidate("a", same, 0.5, 0.5, null), 0.90, null, new Reason(ReasonCode.ColdStart, [])),
            new(new Candidate("b", same, 0.5, 0.5, null), 0.89, null, new Reason(ReasonCode.ColdStart, [])),
            new(new Candidate("c", other, 0.5, 0.5, null), 0.80, null, new Reason(ReasonCode.ColdStart, [])),
        };
        Diversification.Apply(list, 3, maxShare: 1.0).Select(c => c.Candidate.Id).ShouldBe(["a", "c", "b"]);
    }

    [Fact]
    public void Cosine_of_identical_vectors_is_one_and_of_disjoint_ones_zero()
    {
        var a = new Dictionary<string, double> { ["x"] = 1, ["y"] = 2 };
        Diversification.Cosine(a, a).ShouldBe(1d, 1e-9);
        Diversification.Cosine(a, new Dictionary<string, double> { ["z"] = 1 }).ShouldBe(0d);
    }

    [Fact]
    public void Novelty_lowers_the_score_of_places_shown_often()
    {
        var weights = new Dictionary<string, double> { ["history"] = 1 };
        var ranked = Recommender.Rank(TasteProfile.Empty, [new Candidate("fresh", weights, 0.5, 0.5, null), new Candidate("seen", weights, 0.5, 0.5, null, Impressions7d: 9)], TravelMode.Walk);
        ranked[0].Candidate.Id.ShouldBe("fresh");
        (ranked[0].Score - ranked[1].Score).ShouldBe(0.05 * (1 - (1d / 10)), 1e-9);
    }

    [Fact]
    public void Exploration_slots_are_twenty_percent_with_a_minimum_of_one_from_five_places()
    {
        Exploration.Slots(4).ShouldBe(0);
        Exploration.Slots(5).ShouldBe(1);
        Exploration.Slots(9).ShouldBe(1);
        Exploration.Slots(10).ShouldBe(2);
    }

    [Fact]
    public void Without_enough_data_a_category_is_adjacent_when_it_shares_the_level_one_node_with_a_liked_one()
    {
        var vector = new Dictionary<string, double> { ["history.military"] = 0.8, ["nature.coast"] = 0.05 };
        var adjacent = Exploration.AdjacentCategories(vector, null, 0);
        adjacent.ShouldContain("history.maritime");
        adjacent.ShouldContain("history"); // level-1 node of a liked leaf, unknown to the traveler
        adjacent.ShouldNotContain("nature.coast".Replace("coast", "mountain")); // no liked category under nature
        adjacent.ShouldNotContain("history.military"); // already liked
    }

    [Fact]
    public void With_a_matrix_of_enough_travelers_lift_decides()
    {
        var vector = new Dictionary<string, double> { ["history.military"] = 0.8 };
        var lifts = new Dictionary<(string, string), double> { [("nature.coast", "history.military")] = 1.5, [("culture.museums", "history.military")] = 1.0 };
        var adjacent = Exploration.AdjacentCategories(vector, lifts, 80);
        adjacent.ShouldContain("nature.coast");
        adjacent.ShouldNotContain("culture.museums");
        adjacent.ShouldNotContain("history.maritime"); // shared node no longer counts once the matrix exists
    }
}
