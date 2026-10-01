namespace OnVoyage.Recommendation.Engine;

/// <summary>
/// Explainable hybrid ranking of §6.6. MVP-0 subset: no collaborative filtering (its weight is moved to importance and quality,
/// as in the cold start rule of §6.7), novelty counts the impressions of the last 7 days (§6.6) and context is neutral. Pure: no I/O, deterministic.
/// </summary>
public static class Recommender
{
    public static IReadOnlyList<ScoredCandidate> Rank(
        TasteProfile profile,
        IEnumerable<Candidate> candidates,
        TravelMode mode,
        RecommendationOptions? options = null)
    {
        options ??= new RecommendationOptions();
        var (wIm, wImp, wQ) = EffectiveWeights(profile, options);

        return [.. candidates
            .Select(candidate => Score(profile, candidate, mode, options, wIm, wImp, wQ))
            .OrderByDescending(scored => scored.Score)
            .ThenBy(scored => scored.Candidate.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The part of the score that does not depend on where the traveler is, when, or how they move: interest, importance, quality, novelty and
    /// the hidden-gem boost. The device adds <c>Distance</c>, <c>Context</c> and the crowd penalty at each position (§12.4 candidates).
    /// </summary>
    public static double BaseScore(TasteProfile profile, Candidate candidate, RecommendationOptions? options = null)
    {
        options ??= new RecommendationOptions();
        var (wIm, wImp, wQ) = EffectiveWeights(profile, options);
        var im01 = (InterestMatch(profile, candidate) + 1d) / 2d;
        return (wIm * im01)
            + (wImp * candidate.Importance)
            + (wQ * candidate.Quality)
            + (options.Novelty * (1d / (1d + Math.Max(0, candidate.Impressions7d))))
            + (options.GemWeight * (candidate.HiddenGem ? 1d : 0d));
    }

    /// <summary>Control cohort ranking (F-03): <c>0.7 · Importance + 0.3 · Distance</c>.</summary>
    public static IReadOnlyList<Candidate> RankControl(IEnumerable<Candidate> candidates, TravelMode mode) =>
        [.. candidates
            .OrderByDescending(candidate => (0.7 * candidate.Importance) + (0.3 * DistanceTerm(candidate, mode)))
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)];

    internal static (double Interest, double Importance, double Quality) EffectiveWeights(TasteProfile profile, RecommendationOptions options)
    {
        var depthFactor = Math.Clamp((double)profile.Depth / options.ColdStartDepth, 0d, 1d);
        var wIm = options.Interest * depthFactor;
        var freed = (options.Interest - wIm) + options.Collaborative;
        return (wIm, options.Importance + (freed / 2), options.Quality + (freed / 2));
    }

    private static ScoredCandidate Score(
        TasteProfile profile, Candidate candidate, TravelMode mode, RecommendationOptions options, double wIm, double wImp, double wQ)
    {
        var im01 = (InterestMatch(profile, candidate) + 1d) / 2d;
        var crowdPenalty = (Math.Clamp(candidate.CrowdLevel, 1, 5) - 1) / 4d;

        var score = (wIm * im01)
            + (wImp * candidate.Importance)
            + (options.Distance * DistanceTerm(candidate, mode))
            + (wQ * candidate.Quality)
            + (options.Novelty * (1d / (1d + Math.Max(0, candidate.Impressions7d))))
            + (options.Context * 1d)
            - (options.CrowdWeight * crowdPenalty)
            + (options.GemWeight * (candidate.HiddenGem ? 1d : 0d));

        int? compatibility = profile.Depth >= options.ColdStartDepth
            ? Math.Min(options.CompatibilityCap, (int)Math.Round(100d * im01, MidpointRounding.AwayFromZero))
            : null;

        return new ScoredCandidate(candidate, score, compatibility, Explain(profile, candidate, options));
    }

    internal static double InterestMatch(TasteProfile profile, Candidate candidate)
    {
        var total = candidate.Weights.Values.Sum();
        if (total <= 0d)
        {
            return 0d;
        }

        var dot = candidate.Weights.Sum(pair => profile[pair.Key] * pair.Value);
        return Math.Clamp(dot / total, -1d, 1d);
    }

    internal static double DistanceTerm(Candidate candidate, TravelMode mode) =>
        candidate.DistanceMeters is { } meters ? Math.Exp(-meters / RecommendationOptions.ScaleMeters(mode)) : 1d;

    private static Reason Explain(TasteProfile profile, Candidate candidate, RecommendationOptions options)
    {
        if (profile.Depth >= options.ColdStartDepth)
        {
            var top = candidate.Weights
                .Select(pair => (pair.Key, Contribution: profile[pair.Key] * pair.Value))
                .Where(item => item.Contribution > 0d)
                .OrderByDescending(item => item.Contribution)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .Take(2)
                .Select(item => item.Key)
                .ToArray();

            if (top.Length > 0)
            {
                return new Reason(ReasonCode.Categories, top);
            }
        }

        return candidate.HiddenGem && options.Ethical != EthicalLevel.Off
            ? new Reason(ReasonCode.HiddenGem, [])
            : new Reason(ReasonCode.ColdStart, []);
    }
}
