using OnVoyage.Taxonomy;

namespace OnVoyage.Factory.Domain.Classification;

/// <summary>
/// The interest vector <c>p</c> of a place (§6.1): weights in [0, 1] per taxonomy code, the weight of a level-1 node being the
/// maximum of its children. Codes outside the taxonomy are dropped, so a vector can only ever hold valid dimensions.
/// </summary>
public sealed class TaxonomyVector
{
    private static readonly HashSet<string> Valid = [.. Interests.All];

    private TaxonomyVector(IReadOnlyDictionary<string, double> weights) => Weights = weights;

    public static TaxonomyVector Empty { get; } = new(new Dictionary<string, double>());

    public IReadOnlyDictionary<string, double> Weights { get; }

    public bool IsEmpty => Weights.Count == 0;

    /// <summary>True when at least one level-2 category carries weight: the minimum for a rule-based classification (§7.5).</summary>
    public bool CoversALevelTwoCategory => Weights.Keys.Any(code => code.Contains('.', StringComparison.Ordinal));

    public static TaxonomyVector From(IEnumerable<KeyValuePair<string, double>> weights)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (code, rawWeight) in weights)
        {
            var weight = Math.Clamp(double.IsNaN(rawWeight) ? 0d : rawWeight, 0d, 1d);
            if (weight <= 0d || !Valid.Contains(code))
            {
                continue;
            }

            result[code] = Math.Max(result.GetValueOrDefault(code), weight);
            var parent = Interests.LevelOneOf(code);
            if (parent != code)
            {
                result[parent] = Math.Max(result.GetValueOrDefault(parent), weight);
            }
        }

        return new TaxonomyVector(result);
    }

    public double this[string code] => Weights.GetValueOrDefault(code);

    /// <summary>True if any of the codes reaches the threshold; a level-1 code matches through its stored maximum.</summary>
    public bool HasAny(IEnumerable<string> codes, double threshold) => codes.Any(code => this[code] >= threshold);
}

public sealed record RuleWeight(string Code, double Weight);

/// <summary>Mapping from OSM tags (<c>historic=fort</c>, <c>tourism=*</c>) and Wikidata classes (<c>wd:Q23413</c>) to taxonomy weights (§7.5).</summary>
public sealed record ClassificationRuleSet(int Version, IReadOnlyDictionary<string, IReadOnlyList<RuleWeight>> Rules);

public sealed record ClassificationInput(IReadOnlyDictionary<string, string> OsmTags, IReadOnlyCollection<string> WikidataClasses);

public sealed record RuleClassification(TaxonomyVector Vector, IReadOnlyList<string> MatchedRules);

public sealed class RuleClassifier(ClassificationRuleSet rules)
{
    /// <summary>Highest weight per code over every rule that matches the place's tags or classes.</summary>
    public RuleClassification Classify(ClassificationInput input)
    {
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        var matched = new List<string>();

        void Apply(string key)
        {
            if (!rules.Rules.TryGetValue(key, out var entries))
            {
                return;
            }

            matched.Add(key);
            foreach (var entry in entries)
            {
                weights[entry.Code] = Math.Max(weights.GetValueOrDefault(entry.Code), entry.Weight);
            }
        }

        foreach (var (key, value) in input.OsmTags)
        {
            Apply($"{key}={value}");
            Apply($"{key}=*");
        }

        foreach (var wikidataClass in input.WikidataClasses)
        {
            Apply($"wd:{wikidataClass}");
        }

        return new RuleClassification(TaxonomyVector.From(weights), matched);
    }
}

public enum ClassificationOutcome
{
    /// <summary>Rules were enough.</summary>
    Rules,

    /// <summary>The language-model fallback was confident enough.</summary>
    Model,

    /// <summary>Nothing reliable: a person classifies the place in the back office.</summary>
    NeedsReview,
}

/// <summary>Answer of the structured-output fallback. Codes outside the taxonomy are discarded when the vector is built.</summary>
public sealed record ModelClassification(IReadOnlyDictionary<string, double> Weights, double Confidence);

public static class ClassificationDecision
{
    public const double DefaultMinConfidence = 0.6;

    public static (ClassificationOutcome Outcome, TaxonomyVector Vector) Decide(
        RuleClassification byRules, ModelClassification? byModel, double minConfidence = DefaultMinConfidence)
    {
        if (byRules.Vector.CoversALevelTwoCategory)
        {
            return (ClassificationOutcome.Rules, byRules.Vector);
        }

        if (byModel is null)
        {
            return (ClassificationOutcome.NeedsReview, byRules.Vector);
        }

        var vector = TaxonomyVector.From(byModel.Weights);
        return byModel.Confidence >= minConfidence && vector.CoversALevelTwoCategory
            ? (ClassificationOutcome.Model, vector)
            : (ClassificationOutcome.NeedsReview, vector);
    }
}
