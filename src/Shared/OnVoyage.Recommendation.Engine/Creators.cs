namespace OnVoyage.Recommendation.Engine;

/// <summary>
/// What a creator contributes to one place (§6.15): the best creator behind the place and the resulting <c>CreatorSignal</c>. Built by
/// <see cref="CreatorAffinity.Endorse"/>; never carries a follower identity.
/// </summary>
public sealed record CreatorEndorsement(string Handle, bool Followed, double Signal);

/// <summary>A creator that validated a place. <c>Commercial</c> is true when every link to the place comes from content marked "Publicité".</summary>
public sealed record CreatorOnPlace(string Handle, IReadOnlyDictionary<string, double> Vector, bool Followed, bool Commercial);

/// <summary>Affinity and signal of §6.15. Pure.</summary>
public static class CreatorAffinity
{
    public const double FollowedSignal = 1.0;
    public const double SimilarFactor = 0.6;
    public const double SimilarThreshold = 0.5;

    /// <summary><c>A(u, c) = cos(u⁺, c)</c> where <c>u⁺</c> is <c>u</c> with its negative values set to 0; 0 when either vector is empty.</summary>
    public static double Affinity(IReadOnlyDictionary<string, double> traveler, IReadOnlyDictionary<string, double> creator)
    {
        double dot = 0d, normU = 0d, normC = 0d;
        foreach (var (code, value) in traveler)
        {
            var positive = Math.Max(0d, value);
            normU += positive * positive;
            if (positive > 0d && creator.TryGetValue(code, out var c))
            {
                dot += positive * c;
            }
        }

        foreach (var value in creator.Values)
        {
            normC += value * value;
        }

        return normU <= 0d || normC <= 0d ? 0d : Math.Clamp(dot / (Math.Sqrt(normU) * Math.Sqrt(normC)), 0d, 1d);
    }

    /// <summary>
    /// <c>CreatorSignal = max(1.0 if a followed creator validated the place, 0.6 · max A(u, c) over the other creators with A ≥ 0.5, 0)</c>.
    /// A place whose only links come from "Publicité" content receives nothing. Ties go to the handle in ordinal order, so the result is deterministic.
    /// </summary>
    public static CreatorEndorsement? Endorse(IReadOnlyDictionary<string, double> traveler, IEnumerable<CreatorOnPlace> creators)
    {
        var editorial = creators.Where(c => !c.Commercial).OrderBy(c => c.Handle, StringComparer.Ordinal).ToArray();
        if (editorial.FirstOrDefault(c => c.Followed) is { } followed)
        {
            return new CreatorEndorsement(followed.Handle, true, FollowedSignal);
        }

        CreatorEndorsement? best = null;
        foreach (var creator in editorial)
        {
            var affinity = Affinity(traveler, creator.Vector);
            if (affinity >= SimilarThreshold && (best is null || SimilarFactor * affinity > best.Signal))
            {
                best = new CreatorEndorsement(creator.Handle, false, SimilarFactor * affinity);
            }
        }

        return best;
    }

    /// <summary>Creator vector <c>c = Σ w_i · p_i / Σ w_i</c>; <c>w_i</c> is 1 per place, +0.5 with a tip, +0.5 in an itinerary.</summary>
    public static IReadOnlyDictionary<string, double> Vector(IEnumerable<(IReadOnlyDictionary<string, double> Place, double Weight)> places)
    {
        var sum = new Dictionary<string, double>(StringComparer.Ordinal);
        var total = 0d;
        foreach (var (place, weight) in places)
        {
            total += weight;
            foreach (var (code, value) in place)
            {
                sum[code] = sum.GetValueOrDefault(code) + (weight * value);
            }
        }

        return total <= 0d ? new Dictionary<string, double>() : sum.ToDictionary(pair => pair.Key, pair => pair.Value / total, StringComparer.Ordinal);
    }
}
