using OnVoyage.Factory.Domain.Content;

namespace Factory.UnitTests;

public sealed class ContentRulesTests
{
    // ---- status machine

    [Fact]
    public void The_happy_path_walks_the_documented_statuses()
    {
        ContentStatus[] path =
        [
            ContentStatus.Draft, ContentStatus.AiGenerated, ContentStatus.Checked, ContentStatus.Approved,
            ContentStatus.AudioReady, ContentStatus.Published, ContentStatus.Suspended, ContentStatus.Published, ContentStatus.Archived,
        ];

        for (var i = 0; i + 1 < path.Length; i++)
        {
            ContentTransitions.CanMove(path[i], path[i + 1]).ShouldBeTrue($"{path[i]} → {path[i + 1]}");
        }
    }

    [Theory]
    [InlineData(ContentStatus.Draft, ContentStatus.Published)]
    [InlineData(ContentStatus.Checked, ContentStatus.AudioReady)]
    [InlineData(ContentStatus.NeedsReview, ContentStatus.AudioReady)]
    [InlineData(ContentStatus.AiGenerated, ContentStatus.Approved)]
    [InlineData(ContentStatus.Approved, ContentStatus.Published)]
    [InlineData(ContentStatus.Rejected, ContentStatus.Approved)]
    [InlineData(ContentStatus.Archived, ContentStatus.Published)]
    [InlineData(ContentStatus.Published, ContentStatus.Approved)]
    public void Shortcuts_are_refused_notably_audio_never_precedes_text_approval_and_nothing_publishes_unheard(ContentStatus from, ContentStatus to) =>
        ContentTransitions.CanMove(from, to).ShouldBeFalse();

    [Fact]
    public void Rejection_is_only_open_to_checked_and_review_states_and_final()
    {
        foreach (var status in Enum.GetValues<ContentStatus>())
        {
            ContentTransitions.CanMove(status, ContentStatus.Rejected).ShouldBe(status is ContentStatus.Checked or ContentStatus.NeedsReview);
        }

        Enum.GetValues<ContentStatus>().ShouldAllBe(status => !ContentTransitions.CanMove(ContentStatus.Rejected, status));
    }

    [Fact]
    public void A_failed_story_can_only_restart_from_draft_and_only_open_states_are_editable()
    {
        ContentTransitions.CanMove(ContentStatus.Failed, ContentStatus.Draft).ShouldBeTrue();
        ContentTransitions.CanMove(ContentStatus.Failed, ContentStatus.Approved).ShouldBeFalse();
        ContentTransitions.IsEditable(ContentStatus.NeedsReview).ShouldBeTrue();
        ContentTransitions.IsEditable(ContentStatus.Published).ShouldBeFalse();
        ContentTransitions.IsEditable(ContentStatus.Approved).ShouldBeFalse();
    }

    // ---- quotes

    private const string Document = "Le fort Saint-Jean a été construit au XIIe siècle par l’ordre des Hospitaliers.\n\nIl  domine  l'entrée du Vieux-Port.";

    [Theory]
    [InlineData("Le fort Saint-Jean a été construit au XIIe siècle")]
    [InlineData("par l'ordre des Hospitaliers")]
    [InlineData("domine l’entrée du Vieux-Port")]
    [InlineData("  Hospitaliers.\n\nIl domine ")]
    public void A_quote_found_after_normalising_spaces_and_apostrophes_is_exact(string quote) =>
        QuoteValidator.IsExactQuote(quote, Document).ShouldBeTrue();

    [Theory]
    [InlineData("Le fort a été construit au XIIe siècle")]
    [InlineData("construit au XIIIe siècle")]
    [InlineData("LE FORT SAINT-JEAN")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_paraphrase_a_changed_word_or_other_casing_is_not_a_quote(string quote) =>
        QuoteValidator.IsExactQuote(quote, Document).ShouldBeFalse();

    [Fact]
    public void A_quote_longer_than_two_hundred_characters_is_refused_even_if_exact()
    {
        var long_ = string.Join(' ', Enumerable.Repeat("mot", 80));

        QuoteValidator.IsExactQuote(long_, long_).ShouldBeFalse();
        QuoteValidator.IsExactQuote(long_[..200], long_).ShouldBeTrue();
    }

    // ---- conflicts

    private static FactSnapshot Fact(Guid document, FactType type, string statement) => new(Guid.NewGuid(), document, type, statement);

    [Fact]
    public void Two_sources_giving_different_years_for_the_same_event_both_conflict()
    {
        var wiki = Guid.NewGuid();
        var official = Guid.NewGuid();
        var a = Fact(wiki, FactType.Date, "Le fort Saint-Jean a été construit en 1660.");
        var b = Fact(official, FactType.Date, "Le fort Saint-Jean a été construit en 1664.");

        FactConflictDetector.Detect([a, b]).ShouldBe([a.Id, b.Id], ignoreOrder: true);
    }

    [Fact]
    public void Same_year_other_documents_or_unrelated_subjects_do_not_conflict()
    {
        var wiki = Guid.NewGuid();
        var official = Guid.NewGuid();
        var sameYear = Fact(official, FactType.Date, "Le fort Saint-Jean a été construit en 1660 sous Louis XIV.");
        var a = Fact(wiki, FactType.Date, "Le fort Saint-Jean a été construit en 1660.");
        var otherSubject = Fact(official, FactType.Date, "La Vieille Charité accueille les pauvres depuis 1671.");

        FactConflictDetector.Detect([a, sameYear, otherSubject]).ShouldBeEmpty();

        // Two different years inside one document are that document's business (a build date and a restoration date), not a conflict.
        var sameDocument = Fact(wiki, FactType.Date, "Le fort Saint-Jean a été construit en 1664.");
        FactConflictDetector.Detect([a, sameDocument]).ShouldBeEmpty();
    }

    [Fact]
    public void Measures_conflict_on_numbers_and_other_types_never_do()
    {
        var a = Fact(Guid.NewGuid(), FactType.Measure, "La tour du fort mesure 24 mètres de haut.");
        var b = Fact(Guid.NewGuid(), FactType.Measure, "La tour du fort mesure 30 mètres de haut.");
        var c = Fact(Guid.NewGuid(), FactType.Anecdote, "Le fort aurait eu 1660 marches.");
        var d = Fact(Guid.NewGuid(), FactType.Anecdote, "Le fort aurait eu 1700 marches.");

        FactConflictDetector.Detect([a, b, c, d]).ShouldBe([a.Id, b.Id], ignoreOrder: true);
    }

    // ---- verbatim overlap

    private const string Source = "Le fort Saint-Jean est une forteresse située à l'entrée du Vieux-Port de Marseille. Construit à l'origine par les Hospitaliers de l'ordre de Saint-Jean de Jérusalem, il fut remanié sous Louis XIV pour surveiller la ville.";

    [Fact]
    public void A_copied_sentence_fails_both_measures()
    {
        var result = VerbatimOverlapDetector.Compare("Voici une histoire. " + Source, Source);

        result.Passes.ShouldBeFalse();
        result.LongestSharedWords.ShouldBeGreaterThan(20);
        result.FiveGramJaccard.ShouldBeGreaterThan(0.03);
    }

    [Fact]
    public void Both_measures_must_pass_so_a_run_of_five_words_already_fails_the_shingle_overlap()
    {
        const string source = "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima mike november";

        // A run of four shared words makes no shared 5-word shingle: fine.
        var four = VerbatimOverlapDetector.Compare("zéro un alpha bravo charlie delta quatre cinq six", source);
        four.LongestSharedWords.ShouldBe(4);
        four.Passes.ShouldBeTrue();

        // Seven words in a row is under the eight-word limit but shares shingles, so the 0.03 Jaccard limit rejects it.
        var seven = VerbatimOverlapDetector.Compare("zéro un deux alpha bravo charlie delta echo foxtrot golf quatre cinq", source);
        seven.LongestSharedWords.ShouldBe(7);
        seven.FiveGramJaccard.ShouldBeGreaterThan(VerbatimOverlapDetector.MaxJaccard);
        seven.Passes.ShouldBeFalse();

        VerbatimOverlapDetector.Compare("zéro alpha bravo charlie delta echo foxtrot golf hotel india quatre", source).LongestSharedWords.ShouldBe(9);
    }

    [Fact]
    public void An_original_retelling_from_facts_passes()
    {
        const string story = "Imaginez des chevaliers qui gardent la porte de la ville. Au dix-septième siècle, le roi y voit aussi un moyen de tenir les Marseillais à l'œil. Aujourd'hui, une passerelle relie le fort au musée voisin.";

        var result = VerbatimOverlapDetector.Compare(story, Source);

        result.Passes.ShouldBeTrue();
        result.LongestSharedWords.ShouldBeLessThan(8);
    }

    [Fact]
    public void Matching_ignores_case_accents_and_punctuation_and_the_worst_source_decides()
    {
        VerbatimOverlapDetector.Compare("LE FORT SAINT JEAN EST UNE FORTERESSE SITUÉE À L'ENTRÉE DU VIEUX PORT", Source).Passes.ShouldBeFalse();
        VerbatimOverlapDetector.Worst("Une histoire sans rapport avec la source.", [Source, "Autre texte."]).Passes.ShouldBeTrue();
        VerbatimOverlapDetector.Worst("Le fort Saint-Jean est une forteresse située à l'entrée du Vieux-Port", [Source, "Autre texte."]).Passes.ShouldBeFalse();
        VerbatimOverlapDetector.Worst("anything", []).Passes.ShouldBeTrue();
        VerbatimOverlapDetector.Compare("", Source).Passes.ShouldBeTrue();
    }

    // ---- length

    private static string Words(int count) => string.Join(' ', Enumerable.Repeat("mot", count));

    [Theory]
    [InlineData(StoryKind.Standard, 225, true)]
    [InlineData(StoryKind.Standard, 375, true)]
    [InlineData(StoryKind.Standard, 191, true)]
    [InlineData(StoryKind.Standard, 190, false)]
    [InlineData(StoryKind.Standard, 432, true)]
    [InlineData(StoryKind.Standard, 433, false)]
    [InlineData(StoryKind.Anecdote, 40, true)]
    [InlineData(StoryKind.Anecdote, 33, false)]
    [InlineData(StoryKind.Anecdote, 127, true)]
    [InlineData(StoryKind.Anecdote, 128, false)]
    [InlineData(StoryKind.OnboardingClip, 38, true)]
    [InlineData(StoryKind.OnboardingClip, 20, false)]
    public void Length_is_judged_against_the_kind_with_fifteen_percent_of_tolerance(StoryKind kind, int words, bool passes) =>
        (LengthCheck.Check(Words(words), kind) is null).ShouldBe(passes);

    [Fact]
    public void Words_with_apostrophes_and_hyphens_count_once() =>
        LengthCheck.CountWords("L'église Saint-Victor, c'est l'abbaye. 1660 !").ShouldBe(4);

    // ---- style and safety

    [Fact]
    public void Clean_oral_prose_passes()
    {
        StyleCheck.Check("Le fort garde l'entrée du port depuis des siècles. Montez sur le chemin de ronde : la mer s'étend à vos pieds.").ShouldBeEmpty();
        SafetyCheck.Check("Le fort garde l'entrée du port depuis des siècles.").ShouldBeEmpty();
    }

    [Fact]
    public void Bullets_long_sentences_and_brochure_words_are_flagged()
    {
        StyleCheck.Check("- premier point\n- second point").ShouldContain(issue => issue.Check == "style" && issue.Detail.Contains("bullets", StringComparison.Ordinal));
        StyleCheck.Check("1. Premier\n2. Second").ShouldNotBeEmpty();
        StyleCheck.Check(Words(31) + ".").ShouldContain(issue => issue.Detail.Contains("30 words", StringComparison.Ordinal));
        StyleCheck.Check(Words(30) + ".").ShouldBeEmpty();
        StyleCheck.Check("C'est un site INCONTOURNABLE, magnifique et le plus beau de la ville.").Count.ShouldBe(3);
    }

    [Fact]
    public void Sentences_split_on_terminal_punctuation() =>
        StyleCheck.Sentences("Un. Deux ! Trois ? Quatre… Cinq").ShouldBe(["Un.", "Deux !", "Trois ?", "Quatre…", "Cinq"]);

    [Theory]
    [InlineData("Baignez-vous ici en été.")]
    [InlineData("Quittez le sentier pour une vue plus belle.")]
    [InlineData("Cette eau guérit les rhumatismes.")]
    [InlineData("Votez pour le bon candidat.")]
    public void Dangerous_medical_or_partisan_wording_is_flagged_for_a_person(string text) =>
        SafetyCheck.Check(text).ShouldNotBeEmpty();

    // ---- quality score

    [Fact]
    public void The_quality_score_weighs_its_components_as_documented()
    {
        QualityScore.Compute(1, 1, 1, 1, 1).ShouldBe(1d);
        QualityScore.Compute(0.7, 0.9, 1, 1, 0.8).ShouldBe(0.86, 1e-9);
        QualityScore.Compute(0, 0, 0, 0, 0).ShouldBe(0d);
    }

    [Fact]
    public void Too_many_generic_sentences_lower_the_factual_confidence()
    {
        QualityScore.FactualConfidence([0.9, 0.9], 0.2).ShouldBe(0.9, 1e-9);
        QualityScore.FactualConfidence([0.9, 0.9], 0.5).ShouldBe(0.9 * 0.8, 1e-9);
        QualityScore.FactualConfidence([], 0).ShouldBe(0d);
        QualityScore.TextQuality(0).ShouldBe(1d);
        QualityScore.TextQuality(3).ShouldBe(0.7, 1e-9);
        QualityScore.TextQuality(20).ShouldBe(0d);
    }

    // ---- pronunciation

    [Fact]
    public void The_lexicon_replaces_whole_words_only_longest_first()
    {
        var lexicon = new Dictionary<string, string> { ["Cassis"] = "Cassisse", ["Cassis-sur-Mer"] = "Cassisse-sur-Mère", ["MuCEM"] = "Mucème" };

        PronunciationLexicon.Apply("Le MuCEM, puis Cassis-sur-Mer et Cassis. Pas MuCEMX.", lexicon)
            .ShouldBe("Le Mucème, puis Cassisse-sur-Mère et Cassisse. Pas MuCEMX.");
    }
}
