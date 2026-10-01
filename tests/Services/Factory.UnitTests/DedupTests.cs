using OnVoyage.Factory.Domain.Dedup;
using OnVoyage.Factory.Domain.Geo;

namespace Factory.UnitTests;

public sealed class DedupTests
{
    private static readonly GeoPoint Fort = new(43.2960, 5.3618);

    private static DedupSubject Subject(string name, GeoPoint at, string? qid = null, Footprint? footprint = null) =>
        new(Guid.NewGuid(), name, at, qid, footprint);

    private static GeoPoint Offset(GeoPoint from, double meters) => new(from.Latitude + (meters / 111_320d), from.Longitude);

    [Theory]
    [InlineData("Fort Saint-Jean", "fort saint jean")]
    [InlineData("  Château d'If ", "chateau d if")]
    [InlineData("Église Saint-Laurent", "eglise saint laurent")]
    [InlineData("MuCEM", "mucem")]
    public void Names_are_normalised_like_a_search_index(string raw, string expected) =>
        NameSimilarity.Normalize(raw).ShouldBe(expected);

    [Fact]
    public void Identical_names_are_fully_similar_and_unrelated_ones_are_not()
    {
        NameSimilarity.Trigram("Fort Saint-Jean", "Fort Saint Jean").ShouldBe(1d);
        NameSimilarity.Trigram("Fort Saint-Jean", "Plage des Catalans").ShouldBeLessThan(0.1);
        NameSimilarity.Trigram("", "").ShouldBe(0d);
    }

    [Fact]
    public void Similarity_matches_the_pg_trgm_definition_on_a_known_pair()
    {
        // pg_trgm: similarity('word','words') = 0.5714286 (4 shared trigrams of 7 distinct).
        NameSimilarity.Trigram("word", "words").ShouldBe(4d / 7d, 1e-9);
    }

    [Fact]
    public void Accents_and_case_do_not_matter() =>
        NameSimilarity.Trigram("ÉGLISE SAINT-VICTOR", "eglise saint victor").ShouldBe(1d);

    [Fact]
    public void The_same_qid_always_merges_whatever_the_names()
    {
        var detector = new DuplicateDetector();

        var decision = detector.Compare(Subject("Notre-Dame de la Garde", Fort, "Q1"), Subject("La Bonne Mère", Offset(Fort, 900), "q1"));

        decision.Verdict.ShouldBe(DedupVerdict.AutoMerge);
        decision.Reason.ShouldBe("same_qid");
    }

    [Fact]
    public void Two_different_qids_stay_distinct_even_when_close_and_alike()
    {
        var decision = new DuplicateDetector().Compare(Subject("Fort Saint-Jean", Fort, "Q1"), Subject("Fort Saint-Jean", Offset(Fort, 10), "Q2"));

        decision.Verdict.ShouldBe(DedupVerdict.Distinct);
    }

    [Fact]
    public void Close_places_with_the_same_name_merge_automatically()
    {
        var decision = new DuplicateDetector().Compare(Subject("Fort Saint-Jean", Fort), Subject("Fort Saint Jean", Offset(Fort, 30)));

        decision.Verdict.ShouldBe(DedupVerdict.AutoMerge);
        decision.Similarity.ShouldBeGreaterThanOrEqualTo(0.85);
    }

    [Fact]
    public void Close_places_with_a_similar_name_are_only_proposed()
    {
        // "Fort Saint-Jean" vs "Fort Saint-Jean tour": similar but not identical.
        var decision = new DuplicateDetector().Compare(Subject("Fort Saint-Jean", Fort), Subject("Fort Saint-Jean Tour", Offset(Fort, 20)));

        decision.Verdict.ShouldBe(DedupVerdict.Propose);
        decision.Similarity.ShouldBeInRange(0.6, 0.85);
    }

    [Fact]
    public void Distance_is_a_hard_limit()
    {
        var decision = new DuplicateDetector().Compare(Subject("Fort Saint-Jean", Fort), Subject("Fort Saint-Jean", Offset(Fort, 76)));

        decision.Verdict.ShouldBe(DedupVerdict.Distinct);
        new DuplicateDetector().Compare(Subject("Fort Saint-Jean", Fort), Subject("Fort Saint-Jean", Offset(Fort, 74))).Verdict.ShouldBe(DedupVerdict.AutoMerge);
    }

    [Fact]
    public void A_museum_inside_a_fort_is_not_a_duplicate_of_the_fort()
    {
        var fort = Subject("Fort Saint-Jean", Fort, footprint: new Footprint([[new(43.2955, 5.3610), new(43.2955, 5.3626), new(43.2965, 5.3626), new(43.2965, 5.3610)]]));
        var museum = Subject("Fort Saint-Jean musée", new GeoPoint(43.2960, 5.3618));

        new DuplicateDetector().Compare(museum, fort).Verdict.ShouldBe(DedupVerdict.Distinct);
        new DuplicateDetector().Compare(fort, museum).Reason.ShouldBe("inside_footprint");
    }

    [Fact]
    public void A_point_that_duplicates_the_area_it_sits_in_still_merges()
    {
        var area = Subject("Parc Borély", Fort, footprint: new Footprint([[new(43.2955, 5.3610), new(43.2955, 5.3626), new(43.2965, 5.3626), new(43.2965, 5.3610)]]));
        var node = Subject("Parc Borély", new GeoPoint(43.2960, 5.3618));

        new DuplicateDetector().Compare(node, area).Verdict.ShouldBe(DedupVerdict.AutoMerge);
    }

    [Fact]
    public void Footprint_containment_handles_concave_shapes_and_outside_points()
    {
        // An L-shaped building.
        var l = new Footprint([[new(0, 0), new(0, 2), new(1, 2), new(1, 1), new(2, 1), new(2, 0)]]);

        l.Contains(new GeoPoint(0.5, 0.5)).ShouldBeTrue();
        l.Contains(new GeoPoint(0.5, 1.5)).ShouldBeTrue();
        l.Contains(new GeoPoint(1.5, 0.5)).ShouldBeTrue();
        l.Contains(new GeoPoint(1.5, 1.5)).ShouldBeFalse();
        l.Contains(new GeoPoint(3, 3)).ShouldBeFalse();
    }

    [Fact]
    public void The_distance_matches_a_known_value()
    {
        // Vieux-Port to Notre-Dame de la Garde is about 1.4 km.
        new GeoPoint(43.2951, 5.3740).DistanceTo(new GeoPoint(43.2840, 5.3713)).ShouldBeInRange(1200d, 1400d);
    }
}
