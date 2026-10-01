using System.Text.Json;
using OnVoyage.Factory.Domain.Classification;
using OnVoyage.Taxonomy;

namespace Factory.UnitTests;

public sealed class ClassificationTests
{
    private static ClassificationRuleSet Rules() => new(1, new Dictionary<string, IReadOnlyList<RuleWeight>>
    {
        ["historic=fort"] = [new("history.military", 0.9), new("architecture.defensive", 0.9)],
        ["tourism=*"] = [new("culture.museums", 0.2)],
        ["tourism=museum"] = [new("culture.museums", 0.9)],
        ["wd:Q23413"] = [new("architecture.defensive", 0.8), new("history.middle_ages", 0.5)],
        ["bogus=1"] = [new("not.a.code", 0.9)],
    });

    private static ClassificationInput Tags(params (string K, string V)[] tags) =>
        new(tags.ToDictionary(t => t.K, t => t.V), []);

    [Fact]
    public void A_fort_gets_its_children_and_the_derived_level_one_maxima()
    {
        var result = new RuleClassifier(Rules()).Classify(Tags(("historic", "fort"), ("name", "Fort Saint-Jean")));

        result.Vector["history.military"].ShouldBe(0.9);
        result.Vector["architecture.defensive"].ShouldBe(0.9);
        result.Vector["history"].ShouldBe(0.9);
        result.Vector["architecture"].ShouldBe(0.9);
        result.MatchedRules.ShouldBe(["historic=fort"]);
    }

    [Fact]
    public void Wildcards_and_exact_rules_combine_by_maximum()
    {
        var result = new RuleClassifier(Rules()).Classify(Tags(("tourism", "museum")));

        result.Vector["culture.museums"].ShouldBe(0.9);
        result.MatchedRules.ShouldBe(["tourism=museum", "tourism=*"]);
    }

    [Fact]
    public void Wikidata_classes_add_to_osm_rules_without_lowering_them()
    {
        var input = new ClassificationInput(new Dictionary<string, string> { ["historic"] = "fort" }, ["Q23413"]);

        var vector = new RuleClassifier(Rules()).Classify(input).Vector;

        vector["architecture.defensive"].ShouldBe(0.9);
        vector["history.middle_ages"].ShouldBe(0.5);
    }

    [Fact]
    public void Codes_outside_the_taxonomy_never_enter_a_vector()
    {
        var vector = new RuleClassifier(Rules()).Classify(Tags(("bogus", "1"))).Vector;

        vector.IsEmpty.ShouldBeTrue();
        TaxonomyVector.From(new Dictionary<string, double> { ["nonsense"] = 1, ["history.military"] = 0.4 }).Weights.Keys.Order().ShouldBe(["history", "history.military"]);
    }

    [Fact]
    public void Weights_are_clamped_and_non_positive_ones_dropped()
    {
        var vector = TaxonomyVector.From(new Dictionary<string, double> { ["history.military"] = 7, ["nature.coast"] = -1, ["nature.caves"] = double.NaN });

        vector["history.military"].ShouldBe(1d);
        vector.Weights.ContainsKey("nature.coast").ShouldBeFalse();
        vector.Weights.ContainsKey("nature.caves").ShouldBeFalse();
    }

    [Fact]
    public void A_level_one_only_vector_does_not_count_as_covered()
    {
        TaxonomyVector.From(new Dictionary<string, double> { ["history"] = 0.9 }).CoversALevelTwoCategory.ShouldBeFalse();
        TaxonomyVector.From(new Dictionary<string, double> { ["history.local"] = 0.4 }).CoversALevelTwoCategory.ShouldBeTrue();
    }

    [Fact]
    public void Rules_decide_when_they_cover_a_level_two_category()
    {
        var byRules = new RuleClassifier(Rules()).Classify(Tags(("historic", "fort")));

        var (outcome, vector) = ClassificationDecision.Decide(byRules, new ModelClassification(new Dictionary<string, double> { ["nature.coast"] = 1 }, 1));

        outcome.ShouldBe(ClassificationOutcome.Rules);
        vector["nature.coast"].ShouldBe(0d);
    }

    [Fact]
    public void The_model_fallback_is_used_only_when_rules_find_nothing_and_it_is_confident()
    {
        var nothing = new RuleClassifier(Rules()).Classify(Tags(("name", "Mystery")));

        var confident = ClassificationDecision.Decide(nothing, new ModelClassification(new Dictionary<string, double> { ["nature.coast"] = 0.8 }, 0.6));
        var unsure = ClassificationDecision.Decide(nothing, new ModelClassification(new Dictionary<string, double> { ["nature.coast"] = 0.8 }, 0.59));
        var absent = ClassificationDecision.Decide(nothing, null);
        var invented = ClassificationDecision.Decide(nothing, new ModelClassification(new Dictionary<string, double> { ["made.up"] = 1 }, 0.99));

        confident.Outcome.ShouldBe(ClassificationOutcome.Model);
        confident.Vector["nature.coast"].ShouldBe(0.8);
        unsure.Outcome.ShouldBe(ClassificationOutcome.NeedsReview);
        absent.Outcome.ShouldBe(ClassificationOutcome.NeedsReview);
        invented.Outcome.ShouldBe(ClassificationOutcome.NeedsReview);
    }

    [Fact]
    public void The_shipped_mapping_file_only_uses_codes_of_the_taxonomy_and_valid_weights()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "mappings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var valid = Interests.All.ToHashSet();

        var rules = document.RootElement.GetProperty("rules");
        rules.EnumerateObject().Count().ShouldBeGreaterThan(40);
        foreach (var rule in rules.EnumerateObject())
        {
            foreach (var entry in rule.Value.EnumerateArray())
            {
                valid.ShouldContain(entry.GetProperty("code").GetString()!, rule.Name);
                entry.GetProperty("weight").GetDouble().ShouldBeInRange(0.01, 1d, rule.Name);
            }
        }
    }

    [Fact]
    public void Every_tag_family_of_the_specification_is_covered_by_the_shipped_rules()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "mappings.json")));
        var keys = document.RootElement.GetProperty("rules").EnumerateObject().Select(rule => rule.Name).ToHashSet();

        // §7.2 families: heritage, military, tourism, nature, worship, technical, villages, food.
        foreach (var expected in new[]
        {
            "historic=fort", "historic=castle", "historic=battlefield", "military=bunker", "tourism=museum", "tourism=gallery", "tourism=viewpoint",
            "tourism=artwork", "natural=peak", "natural=cave_entrance", "natural=cliff", "natural=beach", "natural=bay", "natural=cape",
            "natural=spring", "natural=waterfall", "natural=rock", "natural=volcano", "geological=*", "leisure=nature_reserve", "boundary=protected_area",
            "amenity=place_of_worship", "man_made=lighthouse", "man_made=windmill", "man_made=watermill", "man_made=bridge", "place=village",
            "place=hamlet", "amenity=marketplace", "craft=winery", "shop=cheese", "shop=wine",
        })
        {
            keys.ShouldContain(expected);
        }
    }
}
