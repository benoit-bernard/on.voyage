namespace OnVoyage.Recommendation.Engine;

/// <summary>A traveler considered as a neighbour: the interest vector <c>u</c> in the order of the taxonomy.</summary>
public sealed record NeighborCandidate(Guid Id, float[] Vector);

/// <summary>A neighbour and its cosine similarity to the traveler. Used inside the computation only; it is never stored or sent.</summary>
public sealed record Neighbor(Guid Id, double Similarity);

/// <summary>The collaborative score of one place for one traveler (§6.5).</summary>
public sealed record CollaborativeScore(Guid PoiId, double Score, int Support);

/// <summary>
/// Collaborative filtering by neighbours of §6.5, pure and deterministic. The neighbours are the <c>K</c> travelers whose vector is the closest in
/// cosine similarity (exact search; the production port may answer the same question with an HNSW index), and
/// <c>CF = Σ sim·r / (Σ |sim| + λ)</c> over the neighbours who rated the place, so a single enthusiastic neighbour never gives a score near 1.
/// </summary>
public static class CollaborativeFiltering
{
    /// <summary>Cosine similarity; 0 when one of the vectors is null (a traveler with no signal has no direction).</summary>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var length = Math.Min(a.Length, b.Length);
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        return normA <= 0d || normB <= 0d ? 0d : Math.Clamp(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)), -1d, 1d);
    }

    /// <summary>
    /// The <paramref name="k"/> candidates closest to <paramref name="target"/>, the traveler themself excluded, ties broken by identifier so a
    /// rerun gives the same neighbours. Only positive similarities count: someone who likes the opposite is not a neighbour.
    /// </summary>
    public static IReadOnlyList<Neighbor> Nearest(Guid targetId, ReadOnlySpan<float> target, IEnumerable<NeighborCandidate> pool, int k, double minSimilarity = 0d)
    {
        var scored = new List<Neighbor>();
        foreach (var candidate in pool)
        {
            if (candidate.Id == targetId)
            {
                continue;
            }

            var similarity = Cosine(target, candidate.Vector);
            if (similarity > minSimilarity)
            {
                scored.Add(new Neighbor(candidate.Id, similarity));
            }
        }

        return [.. scored.OrderByDescending(n => n.Similarity).ThenBy(n => n.Id).Take(k)];
    }

    /// <summary>
    /// <c>CF</c> and <c>support</c> for each place that at least <paramref name="minSupport"/> neighbours rated: a score built from one or two
    /// people is not published, because it would mostly echo what a single person said (k-anonymity, the same idea as the k = 20 of the statistics).
    /// The best <paramref name="max"/> by score are kept.
    /// </summary>
    public static IReadOnlyList<CollaborativeScore> Scores(
        IReadOnlyList<Neighbor> neighbors,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, double>> ratingsByNeighbor,
        double lambda = 5d,
        int minSupport = 3,
        int max = 200)
    {
        var numerator = new Dictionary<Guid, double>();
        var denominator = new Dictionary<Guid, double>();
        var support = new Dictionary<Guid, int>();
        foreach (var neighbor in neighbors)
        {
            if (!ratingsByNeighbor.TryGetValue(neighbor.Id, out var ratings))
            {
                continue;
            }

            foreach (var (poi, rating) in ratings)
            {
                numerator[poi] = numerator.GetValueOrDefault(poi) + (neighbor.Similarity * Math.Clamp(rating, -1d, 1d));
                denominator[poi] = denominator.GetValueOrDefault(poi) + Math.Abs(neighbor.Similarity);
                support[poi] = support.GetValueOrDefault(poi) + 1;
            }
        }

        return [.. support
            .Where(pair => pair.Value >= minSupport)
            .Select(pair => new CollaborativeScore(pair.Key, numerator[pair.Key] / (denominator[pair.Key] + lambda), pair.Value))
            .OrderByDescending(score => score.Score)
            .ThenByDescending(score => score.Support)
            .ThenBy(score => score.PoiId)
            .Take(max)];
    }
}
