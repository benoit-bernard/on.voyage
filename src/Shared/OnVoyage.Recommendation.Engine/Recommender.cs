namespace OnVoyage.Recommendation.Engine;

/// <summary>
/// Explainable hybrid ranking of §6.6. The collaborative term (§6.5) is <c>w_cf · min(1, support / 10) · CF01</c> for a place the neighbours
/// rated; the share of <c>w_cf</c> that this leaves unused (few or no neighbours, or a profile still in cold start, §6.7) goes equally to importance
/// and quality, so the weights always add up to the same total and the content dominates when the neighbours say little. Novelty counts the
/// impressions of the last 7 days (§6.6) and context is neutral. Pure: no I/O, deterministic.
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

        return [.. candidates
            .Select(candidate => Score(profile, candidate, mode, options))
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
        var (wIm, wCf, wImp, wQ) = WeightsFor(profile, candidate, options);
        var im01 = (InterestMatch(profile, candidate) + 1d) / 2d;
        return (wIm * im01)
            + (wCf * Cf01(candidate))
            + (wImp * candidate.Importance)
            + (wQ * candidate.Quality)
            + (options.Novelty * (1d / (1d + Math.Max(0, candidate.Impressions7d))))
            + (options.GemWeight * (candidate.HiddenGem ? 1d : 0d))
            + (options.CreatorWeight * (candidate.Creator?.Signal ?? 0d));
    }

    /// <summary>Control cohort ranking (F-03): <c>0.7 · Importance + 0.3 · Distance</c>.</summary>
    public static IReadOnlyList<Candidate> RankControl(IEnumerable<Candidate> candidates, TravelMode mode) =>
        [.. candidates
            .OrderByDescending(candidate => (0.7 * candidate.Importance) + (0.3 * DistanceTerm(candidate, mode)))
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)];

    /// <summary>The weights when no neighbour has an opinion (all of <c>w_cf</c> goes to importance and quality).</summary>
    internal static (double Interest, double Importance, double Quality) EffectiveWeights(TasteProfile profile, RecommendationOptions options)
    {
        var (wIm, _, wImp, wQ) = WeightsFor(profile, null, options);
        return (wIm, wImp, wQ);
    }

    /// <summary>
    /// §6.7 and §6.5. In cold start (<c>profile_depth &lt; 5</c>) <c>w_im</c> is scaled by <c>depth / 5</c> and <c>w_cf = 0</c>. Otherwise the place gets
    /// <c>w_cf · min(1, support / 10)</c>. What is not used (of <c>w_im</c> and of <c>w_cf</c>) is reported equally on <c>w_imp</c> and <c>w_q</c>.
    /// </summary>
    internal static (double Interest, double Collaborative, double Importance, double Quality) WeightsFor(TasteProfile profile, Candidate? candidate, RecommendationOptions options)
    {
        var depthFactor = Math.Clamp((double)profile.Depth / options.ColdStartDepth, 0d, 1d);
        var wIm = options.Interest * depthFactor;
        var coldStart = profile.Depth < options.ColdStartDepth;
        var wCf = !coldStart && candidate?.Collaborative is { Support: > 0 } signal
            ? options.Collaborative * Math.Min(1d, (double)signal.Support / Math.Max(1, options.CollaborativeSupportFull))
            : 0d;
        var freed = (options.Interest - wIm) + options.Collaborative - wCf;
        return (wIm, wCf, options.Importance + (freed / 2), options.Quality + (freed / 2));
    }

    /// <summary><c>CF01 = (CF + 1) / 2</c>, in [0, 1]; 0 when no neighbour rated the place (its weight is then 0 too).</summary>
    internal static double Cf01(Candidate candidate) => candidate.Collaborative is { } signal ? Math.Clamp((signal.Score + 1d) / 2d, 0d, 1d) : 0d;

    private static ScoredCandidate Score(
        TasteProfile profile, Candidate candidate, TravelMode mode, RecommendationOptions options)
    {
        var (wIm, wCf, wImp, wQ) = WeightsFor(profile, candidate, options);
        var cfContribution = wCf * Cf01(candidate);
        var im01 = (InterestMatch(profile, candidate) + 1d) / 2d;
        var crowdPenalty = (Math.Clamp(candidate.CrowdLevel, 1, 5) - 1) / 4d;

        var score = (wIm * im01)
            + cfContribution
            + (wImp * candidate.Importance)
            + (options.Distance * DistanceTerm(candidate, mode))
            + (wQ * candidate.Quality)
            + (options.Novelty * (1d / (1d + Math.Max(0, candidate.Impressions7d))))
            + (options.Context * 1d)
            - (options.CrowdWeight * crowdPenalty)
            + (options.GemWeight * (candidate.HiddenGem ? 1d : 0d))
            + (options.CreatorWeight * (candidate.Creator?.Signal ?? 0d));

        int? compatibility = profile.Depth >= options.ColdStartDepth
            ? Math.Min(options.CompatibilityCap, (int)Math.Round(100d * im01, MidpointRounding.AwayFromZero))
            : null;

        return new ScoredCandidate(candidate, score, compatibility, Explain(profile, candidate, options, score, cfContribution), cfContribution);
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

    private static Reason Explain(TasteProfile profile, Candidate candidate, RecommendationOptions options, double score, double collaborativeContribution)
    {
        // §6.9: a creator comes second, after "a place you liked" (which Discovery decides) and before the categories.
        if (candidate.Creator is { Signal: > 0d } endorsement)
        {
            return new Reason(endorsement.Followed ? ReasonCode.CreatorFollowed : ReasonCode.CreatorSimilar, [], endorsement.Handle);
        }

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

        // §6.9, third in line: the neighbours explain more than 30 % of the score. Nothing about them is named.
        if (score > 0d && collaborativeContribution / score > options.CollaborativeExplainShare)
        {
            return new Reason(ReasonCode.Collaborative, []);
        }

        return candidate.HiddenGem && options.Ethical != EthicalLevel.Off
            ? new Reason(ReasonCode.HiddenGem, [])
            : new Reason(ReasonCode.ColdStart, []);
    }
}
