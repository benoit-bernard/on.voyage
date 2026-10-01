using OnVoyage.Factory.Domain.Classification;

namespace OnVoyage.Factory.Domain.Scoring;

public enum HeritageStatus { None, Inscribed, Classified }

/// <summary>Parameters of §7.6 (defaults of the specification).</summary>
public sealed record ScoringOptions
{
    public int UnescoPoints { get; init; } = 40;
    public int ClassifiedPoints { get; init; } = 30;
    public int InscribedPoints { get; init; } = 20;
    public double SitelinksCap { get; init; } = 25d;
    public double SitelinksFactor { get; init; } = 8d;
    public double PageviewsCap { get; init; } = 20d;
    public double PageviewsFactor { get; init; } = 4d;
    public int TourismAttractionPoints { get; init; } = 10;
    public int HiddenGemMinImportance { get; init; } = 45;
    public int HiddenGemMaxPercentile { get; init; } = 40;
    public int HiddenGemMaxPeakCrowd { get; init; } = 2;
    public double CategoryThreshold { get; init; } = 0.5;

    /// <summary>Codes (level 1 or 2) whose places get <c>peak = base + 1</c> (annexe E <c>ethics.initial_profile</c>).</summary>
    public IReadOnlyList<string> PeakPlusOneCategories { get; init; } = ["leisure.beaches", "nature.coast", "nature.cliffs_gorges", "nature.viewpoints", "villages"];

    public IReadOnlyList<string> MiddayCategories { get; init; } = ["leisure.beaches", "nature.cliffs_gorges"];
}

public sealed record ImportanceInput(bool IsUnesco, HeritageStatus Heritage, int Sitelinks, long AnnualPageviews, bool IsTourismAttraction, int? EditorialOverride = null);

public static class ImportanceScorer
{
    /// <summary>Importance 0–100: UNESCO, heritage protection, Wikipedia language editions, yearly page views and the tourism tag.</summary>
    public static int Score(ImportanceInput input, ScoringOptions? options = null)
    {
        options ??= new ScoringOptions();
        if (input.EditorialOverride is { } editorial)
        {
            return Math.Clamp(editorial, 0, 100);
        }

        double score = 0;
        if (input.IsUnesco)
        {
            score += options.UnescoPoints;
        }

        score += input.Heritage switch
        {
            HeritageStatus.Classified => options.ClassifiedPoints,
            HeritageStatus.Inscribed => options.InscribedPoints,
            _ => 0,
        };
        score += Math.Min(options.SitelinksCap, options.SitelinksFactor * Math.Log2(1d + Math.Max(0, input.Sitelinks)));
        score += Math.Min(options.PageviewsCap, options.PageviewsFactor * Math.Log10(1d + Math.Max(0L, input.AnnualPageviews)));
        if (input.IsTourismAttraction)
        {
            score += options.TourismAttractionPoints;
        }

        return (int)Math.Round(Math.Min(100d, score), MidpointRounding.AwayFromZero);
    }
}

public static class PopularityPercentiles
{
    /// <summary>Share (0–100) of the destination's places that have strictly fewer page views. A place nobody reads is at 0.</summary>
    public static IReadOnlyDictionary<Guid, int> Compute(IReadOnlyDictionary<Guid, long> annualPageviews)
    {
        var values = annualPageviews.Values.Order().ToArray();
        var result = new Dictionary<Guid, int>(annualPageviews.Count);
        foreach (var (id, views) in annualPageviews)
        {
            if (views <= 0 || values.Length == 0)
            {
                result[id] = 0;
                continue;
            }

            var lower = LowerBound(values, views);
            result[id] = (int)Math.Floor(100d * lower / values.Length);
        }

        return result;
    }

    private static int LowerBound(long[] sorted, long value)
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (sorted[middle] < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}

public sealed record CrowdProfile(int Offpeak, int Shoulder, int Peak, int WeekendDelta, int MiddayDelta);

public static class CrowdProfileBuilder
{
    /// <summary>Initial crowd profile of §7.6 from the popularity percentile; a site on the editorial "saturated" list is at the top.</summary>
    public static CrowdProfile Build(int percentile, TaxonomyVector vector, bool editoriallySaturated, ScoringOptions? options = null)
    {
        options ??= new ScoringOptions();
        if (editoriallySaturated)
        {
            return new CrowdProfile(4, 5, 5, 1, vector.HasAny(options.MiddayCategories, options.CategoryThreshold) ? 1 : 0);
        }

        var baseLevel = percentile switch
        {
            >= 95 => 5,
            >= 80 => 4,
            >= 50 => 3,
            >= 20 => 2,
            _ => 1,
        };

        var peak = vector.HasAny(options.PeakPlusOneCategories, options.CategoryThreshold) ? Math.Min(5, baseLevel + 1) : baseLevel;
        return new CrowdProfile(
            Math.Max(1, baseLevel - 1),
            baseLevel,
            peak,
            percentile >= 50 ? 1 : 0,
            vector.HasAny(options.MiddayCategories, options.CategoryThreshold) ? 1 : 0);
    }
}

public static class HiddenGemRule
{
    public static bool IsHiddenGem(int importance, int percentile, CrowdProfile crowd, ScoringOptions? options = null)
    {
        options ??= new ScoringOptions();
        return importance >= options.HiddenGemMinImportance && percentile <= options.HiddenGemMaxPercentile && crowd.Peak <= options.HiddenGemMaxPeakCrowd;
    }
}
