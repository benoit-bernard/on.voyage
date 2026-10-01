using OnVoyage.Recommendation.Engine.Learning;

namespace OnVoyage.Recommendation.Engine.Tests;

public sealed class InterestLearningTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, DateTimeOffset> NoLocks = [];

    private static readonly Dictionary<string, Dictionary<string, double>> Places = new()
    {
        ["fort"] = new() { ["history"] = 0.9, ["history.military"] = 1.0, ["architecture"] = 0.7, ["architecture.defensive"] = 0.8 },
        ["calanque"] = new() { ["nature"] = 1.0, ["nature.coast"] = 1.0 },
        ["mucem"] = new() { ["culture"] = 0.9, ["culture.contemporary_art"] = 0.9 },
        ["mucem2"] = new() { ["culture"] = 0.8, ["culture.museums"] = 0.8 },
        ["mucem3"] = new() { ["culture"] = 0.7, ["culture.painting"] = 0.7 },
        ["empty"] = [],
    };

    private static IReadOnlyDictionary<string, double>? Weights(string id) => Places.GetValueOrDefault(id);

    private static Interaction Event(string kind, string? poi, int minutes, double value = 0, string? category = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), kind, poi, T0.AddMinutes(minutes), value, category);

    private static LearnedProfile Run(params Interaction[] events) => InterestLearning.Replay(events, Weights, NoLocks);

    [Fact]
    public void A_like_moves_each_dimension_by_eta_times_weight_with_damping()
    {
        var first = Run(Event(InteractionKinds.Like, "fort", 0));
        first.Vector["history.military"].ShouldBe(0.15, 1e-9);
        first.Vector["history"].ShouldBe(0.15 * 0.9, 1e-9);

        // The second like moves less: (1 − |u|) slows learning on a strong affinity.
        var second = Run(Event(InteractionKinds.Like, "fort", 0), Event(InteractionKinds.Like, "fort", 1));
        second.Vector["history.military"].ShouldBe(0.15 + (0.15 * (1 - 0.15)), 1e-9);
    }

    [Fact]
    public void Onboarding_uses_the_faster_rate_and_the_asymmetric_dislike()
    {
        var profile = InterestLearning.Onboarding(
            [Event(InteractionKinds.OnboardingUp, "fort", 0), Event(InteractionKinds.OnboardingUp, "calanque", 1), Event(InteractionKinds.OnboardingDown, "mucem", 2)],
            Weights);
        profile.Vector["history.military"].ShouldBeGreaterThan(0);
        profile.Vector["nature.coast"].ShouldBe(0.35, 1e-9);
        profile.Vector["culture.contemporary_art"].ShouldBeLessThan(0);
        profile.Vector["culture.contemporary_art"].ShouldBe(-0.35 * 0.8 * 0.9, 1e-9);
    }

    [Fact]
    public void A_category_choice_sets_the_dimension_directly()
    {
        var profile = Run(Event(InteractionKinds.OnboardingCategory, null, 0, 1, "nature"), Event(InteractionKinds.OnboardingCategory, null, 1, -1, "culture"));
        profile.Vector["nature"].ShouldBe(0.6);
        profile.Vector["culture"].ShouldBe(-0.6);
        profile.ProfileDepth.ShouldBe(2);
    }

    [Fact]
    public void Dislike_of_a_place_excludes_it_and_barely_moves_the_vector()
    {
        var profile = Run(Event(InteractionKinds.DislikePoi, "mucem", 0));
        profile.Excluded.ShouldContain("mucem");
        profile.Ratings["mucem"].ShouldBe(-1);
        profile.Vector["culture.contemporary_art"].ShouldBe(0.15 * -0.18 * 0.9, 1e-9);
    }

    [Fact]
    public void Dislike_of_a_category_uses_the_category_vector()
    {
        var profile = Run(Event(InteractionKinds.DislikeCategory, "mucem", 0, 0, "culture"));
        profile.Vector["culture"].ShouldBe(-0.15, 1e-9);
        profile.Vector["culture.museums"].ShouldBe(-0.075, 1e-9);
        profile.Vector.ShouldNotContainKey("nature");
    }

    [Fact]
    public void Three_rejections_in_one_category_within_thirty_days_add_one_category_signal()
    {
        var two = Run(Event(InteractionKinds.DislikePoi, "mucem", 0), Event(InteractionKinds.DislikePoi, "mucem2", 1));
        var three = Run(Event(InteractionKinds.DislikePoi, "mucem", 0), Event(InteractionKinds.DislikePoi, "mucem2", 1), Event(InteractionKinds.DislikePoi, "mucem3", 2));
        two.Vector.ContainsKey("culture.museums").ShouldBeTrue();
        three.Vector["culture"].ShouldBeLessThan(two.Vector["culture"] - 0.05);

        // A fourth rejection does not apply it again.
        var four = Run(Event(InteractionKinds.DislikePoi, "mucem", 0), Event(InteractionKinds.DislikePoi, "mucem2", 1), Event(InteractionKinds.DislikePoi, "mucem3", 2), Event(InteractionKinds.DislikePoi, "mucem", 3));
        (three.Vector["culture"] - four.Vector["culture"]).ShouldBeLessThan(0.04); // only the small place step, not another category step (~0.09)
    }

    [Fact]
    public void Rejections_thirty_one_days_apart_do_not_count_together()
    {
        var spread = Run(Event(InteractionKinds.DislikePoi, "mucem", 0), Event(InteractionKinds.DislikePoi, "mucem2", 60 * 24 * 20), Event(InteractionKinds.DislikePoi, "mucem3", 60 * 24 * 45));
        var dense = Run(Event(InteractionKinds.DislikePoi, "mucem", 0), Event(InteractionKinds.DislikePoi, "mucem2", 1), Event(InteractionKinds.DislikePoi, "mucem3", 2));
        spread.Vector["culture"].ShouldBeGreaterThan(dense.Vector["culture"]);
    }

    [Fact]
    public void Locked_dimensions_do_not_move()
    {
        var locks = new Dictionary<string, DateTimeOffset> { ["history.military"] = T0.AddDays(30) };
        var profile = InterestLearning.Replay([Event(InteractionKinds.Like, "fort", 0)], Weights, locks);
        profile.Vector.ContainsKey("history.military").ShouldBeFalse();
        profile.Vector["architecture.defensive"].ShouldBeGreaterThan(0);

        // Once the lock has ended the dimension learns again.
        var after = InterestLearning.Replay([Event(InteractionKinds.Like, "fort", 60 * 24 * 31)], Weights, locks);
        after.Vector["history.military"].ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Profile_depth_follows_the_weights_of_section_6_13()
    {
        var profile = Run(
            Event(InteractionKinds.OnboardingUp, "fort", 0),
            Event(InteractionKinds.Like, "fort", 1),
            Event(InteractionKinds.Listen80, "fort", 2),
            Event(InteractionKinds.Save, "calanque", 3),
            Event(InteractionKinds.Visit, "calanque", 4, 0.8),
            Event(InteractionKinds.Visit, "fort", 5, 0.5),
            Event(InteractionKinds.Impression, "fort", 6));
        profile.ProfileDepth.ShouldBe(1 + 3 + 1 + 2 + 3);
    }

    [Fact]
    public void A_visit_scales_with_its_confidence_and_an_impression_changes_nothing()
    {
        var low = Run(Event(InteractionKinds.Visit, "calanque", 0, 0.4));
        var high = Run(Event(InteractionKinds.Visit, "calanque", 0, 0.9));
        high.Vector["nature.coast"].ShouldBeGreaterThan(low.Vector["nature.coast"]);
        Run(Event(InteractionKinds.Impression, "calanque", 0)).Vector.ShouldBeEmpty();
    }

    [Fact]
    public void The_same_event_id_counts_once()
    {
        var id = Guid.NewGuid();
        var once = Run(Event(InteractionKinds.Like, "fort", 0, id: id));
        var twice = Run(Event(InteractionKinds.Like, "fort", 0, id: id), Event(InteractionKinds.Like, "fort", 0, id: id));
        twice.Vector["history.military"].ShouldBe(once.Vector["history.military"]);
        twice.ProfileDepth.ShouldBe(once.ProfileDepth);
    }

    [Fact]
    public void The_result_depends_on_event_time_not_on_arrival_order()
    {
        var a = Event(InteractionKinds.Like, "fort", 0);
        var b = Event(InteractionKinds.DislikePoi, "fort", 5);
        var c = Event(InteractionKinds.Save, "fort", 3);
        var inOrder = Run(a, c, b);
        var shuffled = Run(b, a, c);
        shuffled.Vector.ShouldBe(inOrder.Vector);
    }

    [Fact]
    public void Property_the_vector_stays_within_minus_one_and_one_and_exclusion_is_definitive()
    {
        var random = new Random(42);
        var kinds = InteractionKinds.All.Where(k => k is not InteractionKinds.OnboardingCategory and not InteractionKinds.DislikeCategory).ToArray();
        var ids = Places.Keys.ToArray();
        for (var round = 0; round < 200; round++)
        {
            var events = Enumerable.Range(0, 60).Select(i => Event(kinds[random.Next(kinds.Length)], ids[random.Next(ids.Length)], random.Next(0, 5000), random.NextDouble())).ToArray();
            var profile = Run(events);
            profile.Vector.Values.ShouldAllBe(v => v >= -1d && v <= 1d);
            foreach (var rejected in events.Where(e => e.Kind == InteractionKinds.DislikePoi).Select(e => e.PoiId!))
            {
                profile.Excluded.ShouldContain(rejected);
            }
        }
    }

    [Fact]
    public void Property_a_like_never_lowers_the_match_of_the_liked_place()
    {
        var random = new Random(7);
        var ids = Places.Keys.Where(k => k != "empty").ToArray();
        for (var round = 0; round < 200; round++)
        {
            var history = Enumerable.Range(0, random.Next(0, 20)).Select(i => Event(InteractionKinds.Like, ids[random.Next(ids.Length)], i)).ToList();
            var liked = ids[random.Next(ids.Length)];
            var before = Run([.. history]);
            history.Add(Event(InteractionKinds.Like, liked, 100));
            var after = Run([.. history]);
            var candidate = new Candidate(liked, Places[liked], 0.5, 0.5, null);
            Recommender.InterestMatch(new TasteProfile(after.Vector, 10), candidate)
                .ShouldBeGreaterThanOrEqualTo(Recommender.InterestMatch(new TasteProfile(before.Vector, 10), candidate) - 1e-12);
        }
    }

    [Fact]
    public void Dominant_category_is_the_largest_level_one_value()
    {
        InterestLearning.DominantCategory(Places["fort"]).ShouldBe("history");
        InterestLearning.DominantCategory(Places["empty"]).ShouldBeNull();
    }
}
