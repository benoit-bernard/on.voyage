using OnVoyage.Recommendation.Engine.Learning;

namespace OnVoyage.Recommendation.Engine.Tests;

public sealed class CreatorSignalTests
{
    private static Dictionary<string, double> V(params (string Code, double Value)[] items) => items.ToDictionary(i => i.Code, i => i.Value);

    private static CreatorOnPlace Creator(string handle, Dictionary<string, double> vector, bool followed = false, bool commercial = false) =>
        new(handle, vector, followed, commercial);

    [Fact]
    public void Affinity_is_the_cosine_of_the_positive_part_of_the_traveler_vector()
    {
        var traveler = V(("history", 1d), ("nature", -1d));

        CreatorAffinity.Affinity(traveler, V(("history", 1d))).ShouldBe(1d, 1e-9);
        CreatorAffinity.Affinity(traveler, V(("nature", 1d))).ShouldBe(0d, 1e-9); // the dislike is ignored, not inverted
        CreatorAffinity.Affinity(traveler, V(("history", 1d), ("nature", 1d))).ShouldBe(Math.Sqrt(0.5), 1e-9);
        CreatorAffinity.Affinity(V(), V(("history", 1d))).ShouldBe(0d);
        CreatorAffinity.Affinity(traveler, V()).ShouldBe(0d);
    }

    [Fact]
    public void A_followed_creator_gives_exactly_one_whatever_the_affinity()
    {
        var endorsement = CreatorAffinity.Endorse(V(("history", 1d)), [Creator("marie", V(("nature", 1d)), followed: true)]);

        endorsement.ShouldNotBeNull();
        endorsement.Signal.ShouldBe(1d);
        endorsement.Followed.ShouldBeTrue();
        endorsement.Handle.ShouldBe("marie");
    }

    [Fact]
    public void An_unfollowed_creator_counts_at_sixty_percent_of_the_affinity_and_only_from_one_half()
    {
        var traveler = V(("history", 1d));

        var close = CreatorAffinity.Endorse(traveler, [Creator("marco", V(("history", 1d)))]);
        close!.Signal.ShouldBe(0.6, 1e-9);
        close.Followed.ShouldBeFalse();

        // cos = 0.447 < 0.5: no signal.
        CreatorAffinity.Endorse(traveler, [Creator("far", V(("history", 1d), ("nature", 2d)))]).ShouldBeNull();
    }

    [Fact]
    public void The_best_unfollowed_creator_wins_and_ties_go_to_the_handle()
    {
        var traveler = V(("history", 1d));

        var winner = CreatorAffinity.Endorse(traveler, [Creator("zoe", V(("history", 1d), ("nature", 0.5))), Creator("ana", V(("history", 1d))), Creator("bea", V(("history", 1d)))]);

        winner!.Handle.ShouldBe("ana");
        winner.Signal.ShouldBe(0.6, 1e-9);
    }

    [Fact]
    public void Advertising_content_gives_no_signal_even_to_a_followed_creator()
    {
        var traveler = V(("history", 1d));

        CreatorAffinity.Endorse(traveler, [Creator("paid", V(("history", 1d)), followed: true, commercial: true)]).ShouldBeNull();

        var editorial = CreatorAffinity.Endorse(traveler, [Creator("paid", V(("history", 1d)), commercial: true), Creator("honest", V(("history", 1d)))]);
        editorial!.Handle.ShouldBe("honest");
    }

    [Fact]
    public void The_creator_vector_is_the_weighted_mean_of_the_place_vectors()
    {
        var vector = CreatorAffinity.Vector([(V(("history", 1d)), 1d), (V(("nature", 1d)), 1.5)]);

        vector["history"].ShouldBe(0.4, 1e-9);
        vector["nature"].ShouldBe(0.6, 1e-9);
        CreatorAffinity.Vector([]).ShouldBeEmpty();
    }

    [Fact]
    public void The_signal_enters_the_score_with_weight_one_tenth()
    {
        var plain = new Candidate("a", V(("history", 1d)), 0.5, 0.5, null);
        var endorsed = plain with { Creator = new CreatorEndorsement("marie", true, 1d) };
        var profile = new TasteProfile(V(("history", 0.5)), 10);

        (Recommender.BaseScore(profile, endorsed) - Recommender.BaseScore(profile, plain)).ShouldBe(0.10, 1e-9);
        new RecommendationOptions().CreatorWeight.ShouldBe(0.10);
    }

    [Fact]
    public void A_creator_explains_the_pick_before_the_categories_and_names_the_handle()
    {
        var profile = new TasteProfile(V(("history", 0.9)), 12);
        var followed = new Candidate("f", V(("history", 1d)), 0.5, 0.5, null, Creator: new CreatorEndorsement("marie", true, 1d));
        var similar = new Candidate("s", V(("history", 1d)), 0.5, 0.5, null, Creator: new CreatorEndorsement("marco", false, 0.6));
        var none = new Candidate("n", V(("history", 1d)), 0.5, 0.5, null);

        var ranked = Recommender.Rank(profile, [none, similar, followed], TravelMode.Walk).ToDictionary(s => s.Candidate.Id);

        ranked["f"].Reason.Code.ShouldBe(ReasonCode.CreatorFollowed);
        ranked["f"].Reason.Creator.ShouldBe("marie");
        ranked["s"].Reason.Code.ShouldBe(ReasonCode.CreatorSimilar);
        ranked["s"].Reason.Creator.ShouldBe("marco");
        ranked["n"].Reason.Code.ShouldBe(ReasonCode.Categories);
    }

    [Fact]
    public void Following_a_creator_replays_once_with_the_frozen_vector_and_an_empty_vector_moves_nothing()
    {
        var follow = new Interaction(Guid.NewGuid(), InteractionKinds.FollowCreator, null, DateTimeOffset.UnixEpoch.AddDays(1), Weights: V(("history", 0.5), ("nature", 0.5)));
        var empty = follow with { ClientEventId = Guid.NewGuid(), Weights = null };

        var learned = InterestLearning.Replay([follow, empty, follow], _ => null, new Dictionary<string, DateTimeOffset>());

        // eta 0.15 * intensity 0.2 * weight 0.5 = 0.015; the duplicate (same ClientEventId) is ignored.
        learned.Vector["history"].ShouldBe(0.015, 1e-9);
        learned.Vector["nature"].ShouldBe(0.015, 1e-9);
        learned.ProfileDepth.ShouldBe(0);
        InteractionKinds.All.ShouldNotContain(InteractionKinds.FollowCreator);
    }

    [Fact]
    public void Property_the_signal_stays_below_sixty_percent_without_a_follow_and_advertising_gives_none()
    {
        var random = new Random(7);
        string[] codes = ["history", "nature", "culture", "gastronomy"];
        for (var round = 0; round < 300; round++)
        {
            var traveler = codes.ToDictionary(c => c, _ => (random.NextDouble() * 2) - 1);
            var creators = Enumerable.Range(0, random.Next(0, 5))
                .Select(i => Creator($"c{i}", codes.ToDictionary(c => c, _ => random.NextDouble()), followed: false, commercial: random.Next(3) == 0))
                .ToArray();

            var signal = CreatorAffinity.Endorse(traveler, creators)?.Signal ?? 0d;

            signal.ShouldBeInRange(0d, 0.6 + 1e-9);
            (CreatorAffinity.Endorse(traveler, creators)?.Signal ?? 0d).ShouldBe(signal);
            if (creators.All(c => c.Commercial))
            {
                signal.ShouldBe(0d);
            }
        }
    }
}
