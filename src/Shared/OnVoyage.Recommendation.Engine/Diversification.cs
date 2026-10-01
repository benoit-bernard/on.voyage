using OnVoyage.Recommendation.Engine.Learning;

namespace OnVoyage.Recommendation.Engine;

/// <summary>
/// Diversification of §6.11: Maximal Marginal Relevance over the first 50 candidates,
/// <c>MMR(i) = λ·Score(i) − (1 − λ)·max_{j chosen} cos(p_i, p_j)</c> with λ = 0.7, and at most <c>max(1, ⌈0.4·n⌉)</c> places of one dominant
/// level-1 category in a list of <c>n</c>. Deterministic: ties break on the identifier.
/// </summary>
public static class Diversification
{
    public const int Pool = 50;

    public static IReadOnlyList<ScoredCandidate> Apply(IReadOnlyList<ScoredCandidate> ranked, int count, double lambda = 0.7, double maxShare = 0.4)
    {
        var pool = ranked.Take(Pool).ToList();
        var ceiling = Math.Max(1, (int)Math.Ceiling(maxShare * count));
        var perCategory = new Dictionary<string, int>(StringComparer.Ordinal);
        var chosen = new List<ScoredCandidate>();

        while (chosen.Count < count && pool.Count > 0)
        {
            var best = pool
                .Where(c => InterestLearning.DominantCategory(c.Candidate.Weights) is not { } category || perCategory.GetValueOrDefault(category) < ceiling)
                .Select(c => (Item: c, Value: (lambda * c.Score) - ((1d - lambda) * (chosen.Count == 0 ? 0d : chosen.Max(j => Cosine(c.Candidate.Weights, j.Candidate.Weights))))))
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Item.Candidate.Id, StringComparer.Ordinal)
                .Select(x => x.Item)
                .FirstOrDefault();

            if (best is null)
            {
                break; // every remaining place is in a category that reached its ceiling
            }

            pool.Remove(best);
            chosen.Add(best);
            if (InterestLearning.DominantCategory(best.Candidate.Weights) is { } dominant)
            {
                perCategory[dominant] = perCategory.GetValueOrDefault(dominant) + 1;
            }
        }

        return chosen;
    }

    public static double Cosine(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b)
    {
        double dot = 0, na = 0, nb = 0;
        foreach (var (code, value) in a)
        {
            na += value * value;
            if (b.TryGetValue(code, out var other))
            {
                dot += value * other;
            }
        }

        foreach (var value in b.Values)
        {
            nb += value * value;
        }

        return na == 0d || nb == 0d ? 0d : dot / Math.Sqrt(na * nb);
    }
}
