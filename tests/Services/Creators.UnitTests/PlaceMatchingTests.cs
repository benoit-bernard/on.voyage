using OnVoyage.Creators.Domain;

namespace Creators.UnitTests;

public sealed class PlaceMatchingTests
{
    private static IReadOnlyList<PlaceMatch> Run(string caption, IReadOnlyList<Chapter>? chapters = null, params Guid[] destinations) =>
        GeoCorpus.RunAsync("Titre", caption, chapters, destinations).GetAwaiter().GetResult();

    private static double Confidence(IReadOnlyList<PlaceMatch> matches, string poiName) =>
        matches.Where(match => match.Best?.Poi.NameFr == poiName).Select(match => match.Best!.Confidence).DefaultIfEmpty(0).Max();

    // ---- similarity

    [Theory]
    [InlineData("Notre-Dame de la Garde", "notre dame de la garde")]
    [InlineData("  Château d'If ", "chateau d if")]
    [InlineData("L'Estaque", "estaque")]
    [InlineData("Le Vieux-Port", "vieux port")]
    [InlineData("Île du Frioul", "ile du frioul")]
    public void Names_are_compared_without_case_accents_articles_or_punctuation(string name, string expected) => PlaceMatcher.Normalize(name).ShouldBe(expected);

    [Fact]
    public void Trigram_similarity_follows_pg_trgm()
    {
        PlaceMatcher.TrigramSimilarity("gordes", "gordes").ShouldBe(1d);
        PlaceMatcher.TrigramSimilarity("gordes", "gorde").ShouldBeGreaterThan(0.5);
        PlaceMatcher.TrigramSimilarity("gordes", "cassis").ShouldBeLessThan(0.1);
        PlaceMatcher.TrigramSimilarity(string.Empty, "gordes").ShouldBe(0d);
        PlaceMatcher.TrigramSimilarity("cat", "cat").ShouldBe(1d);
        PlaceMatcher.TrigramSimilarity("word", "two words").ShouldBe(0.36363636363636365, 1e-9); // the value pg_trgm gives for similarity('word', 'two words')
    }

    // ---- confidence

    [Fact]
    public async Task A_chapter_with_the_exact_name_of_a_place_is_a_sure_match_with_its_timestamp()
    {
        // « 02:15 Gordes · 05:40 Roussillon » (F-28 acceptance criterion): two associations with their timestamps.
        var matches = await GeoCorpus.RunAsync("Provence en 3 jours", "Mes lieux préférés", [new Chapter(135, "Gordes"), new Chapter(340, "Roussillon")]);

        var gordes = matches.Single(match => match.Best?.Poi.NameFr == "Gordes");
        var roussillon = matches.Single(match => match.Best?.Poi.NameFr == "Roussillon");
        (gordes.Mention.StartSeconds, gordes.Best!.Confidence).ShouldBe((135, 1d));
        (roussillon.Mention.StartSeconds, roussillon.Best!.Confidence).ShouldBe((340, 1d));
        gordes.Best.Signals.ShouldContain("chapter");
    }

    [Fact]
    public void An_ambiguous_notre_dame_is_proposed_below_the_bulk_threshold_for_manual_review()
    {
        // « Notre-Dame » alone in the content of a creator from Marseille: Notre-Dame de la Garde, with a confidence under 0.9 (F-28 acceptance criterion).
        var matches = Run("Notre-Dame ce matin", null, GeoCorpus.Marseille);

        var best = matches.Single(match => match.Mention.Name == "Notre-Dame").Best!;
        best.Poi.NameFr.ShouldBe("Notre-Dame de la Garde");
        best.Confidence.ShouldBeLessThan(0.9);
        best.Confidence.ShouldBeGreaterThanOrEqualTo(PlaceMatcher.MinProposal);
        best.Signals.ShouldContain("partial");
        best.Signals.ShouldContain("ambiguous"); // Notre-Dame de Paris is a near rival
    }

    [Fact]
    public void A_partial_name_never_goes_above_the_partial_cap_even_without_a_rival()
    {
        var matches = Run("Randonnée vers Sormiou", null, GeoCorpus.Marseille);

        Confidence(matches, "Calanque de Sormiou").ShouldBeInRange(0.5, PlaceMatcher.PartialCap);
    }

    [Fact]
    public void An_exact_name_in_a_destination_the_creator_covers_is_a_sure_match_and_the_city_adds_to_it()
    {
        var matches = Run("Un après-midi à Gordes", null, GeoCorpus.Provence);

        var gordes = matches.Single(match => match.Best?.Poi.NameFr == "Gordes").Best!;
        gordes.Confidence.ShouldBe(1d);
        gordes.Signals.ShouldContain("context");
    }

    [Fact]
    public void A_city_that_contradicts_the_place_lowers_the_confidence()
    {
        var poi = GeoCorpus.Directory.Single(item => item.NameFr == "Arles");
        var withCity = new PlaceMention("Arles", null, "Paris", "Arles, Paris", 0.8);
        var withoutCity = new PlaceMention("Arles", null, null, "Arles", 0.8);

        var contradicted = PlaceMatcher.Match([(withCity, [poi])], new MatchContext(new HashSet<Guid>())).Single().Best!;
        var plain = PlaceMatcher.Match([(withoutCity, [poi])], new MatchContext(new HashSet<Guid>())).Single().Best!;

        contradicted.Confidence.ShouldBeLessThan(plain.Confidence);
        contradicted.Signals.ShouldContain("city_mismatch");
    }

    [Fact]
    public void The_other_places_of_the_content_decide_between_two_homonyms()
    {
        // « Notre-Dame » with Montmartre and the Tour Eiffel: Paris wins over Marseille.
        var matches = Run("Montmartre, la Tour Eiffel et Notre-Dame", null);

        matches.Single(match => match.Mention.Name == "Notre-Dame").Best!.Poi.NameFr.ShouldBe("Notre-Dame de Paris");
    }

    [Fact]
    public void Two_places_with_the_same_name_are_never_a_sure_match()
    {
        var first = new PoiEntry(Guid.NewGuid(), Guid.NewGuid(), "a", "Place Carnot", null, [], "Ville A", 50, true, 1);
        var second = first with { PoiId = Guid.NewGuid(), DestinationId = Guid.NewGuid(), DestinationSlug = "b", City = "Ville B" };

        var match = PlaceMatcher.Match([(new PlaceMention("Place Carnot", null, null, "Place Carnot", 0.9), [first, second])], new MatchContext(new HashSet<Guid>())).Single();

        match.Best!.Confidence.ShouldBe(PlaceMatcher.AmbiguousCap);
        match.Alternatives.Count.ShouldBe(1);
    }

    [Fact]
    public void An_unknown_place_has_no_candidate_and_a_piece_of_a_phrase_only_counts_as_a_whole_name()
    {
        Run("Un détour par Bédoin", null, GeoCorpus.Provence).ShouldAllBe(match => match.Best == null || match.Best.Poi.NameFr != "Bédoin");
        var piece = new PlaceMention("Saint", null, null, "Saint", 0.5, null, ExactOnly: true);

        PlaceMatcher.Match([(piece, GeoCorpus.Directory.Where(poi => poi.NameFr.Contains("Saint", StringComparison.Ordinal)).ToList())], new MatchContext(new HashSet<Guid>())).Single().Best.ShouldBeNull();
    }

    // ---- the annotated captions (T-1209: precision >= 0.9 above the threshold)

    [Fact]
    public void Above_the_bulk_threshold_the_proposals_of_fifty_annotated_captions_are_at_least_nine_in_ten_right()
    {
        int sure = 0, right = 0, expectedTotal = 0, found = 0;
        List<string> wrong = [];
        foreach (var (caption, expected) in GeoCorpus.Captions)
        {
            var matches = Run(caption, null, GeoCorpus.Marseille);
            var proposals = matches.Where(match => match.Best is { Confidence: >= 0.9 }).Select(match => match.Best!.Poi.NameFr).Distinct().ToList();
            sure += proposals.Count;
            right += proposals.Count(expected.Contains);
            wrong.AddRange(proposals.Where(name => !expected.Contains(name)).Select(name => $"{name} <- {caption}"));
            var any = matches.Where(match => match.Best is { Confidence: >= PlaceMatcher.MinProposal }).Select(match => match.Best!.Poi.NameFr).Distinct().ToList();
            expectedTotal += expected.Length;
            found += expected.Count(any.Contains);
        }

        GeoCorpus.Captions.Count.ShouldBe(50);
        sure.ShouldBeGreaterThanOrEqualTo(35);
        wrong.ShouldBeEmpty(string.Join('\n', wrong));
        (right / (double)sure).ShouldBeGreaterThanOrEqualTo(0.9);
        (found / (double)expectedTotal).ShouldBeGreaterThanOrEqualTo(0.85); // recall of proposals of any confidence, for review
    }

    [Fact]
    public void Captions_about_nothing_give_no_proposal_at_all()
    {
        foreach (var (caption, expected) in GeoCorpus.Captions.Where(item => item.Expected.Length == 0))
        {
            Run(caption, null, GeoCorpus.Marseille).ShouldAllBe(match => match.Best == null, caption);
        }
    }
}
