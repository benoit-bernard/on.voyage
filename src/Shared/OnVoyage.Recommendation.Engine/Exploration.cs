using OnVoyage.Taxonomy;

namespace OnVoyage.Recommendation.Engine;

/// <summary>
/// Adjacent categories of §6.10: a category <c>k</c> the traveler knows little about (<c>|u[k]| &lt; 0.2</c>) that tends to be liked together with
/// a category they like (<c>u[k'] ≥ 0.5</c>, <c>lift(k, k') &gt; 1.1</c> in the global co-appreciation matrix). While the matrix lacks data
/// (&lt; 50 travelers) a category is adjacent when it shares its level-1 node with a liked category.
/// </summary>
public static class Exploration
{
    public const int MinTravelersForMatrix = 50;

    public static HashSet<string> AdjacentCategories(
        IReadOnlyDictionary<string, double> vector,
        IReadOnlyDictionary<(string A, string B), double>? lifts,
        int travelersInMatrix)
    {
        var liked = Interests.All.Where(code => vector.GetValueOrDefault(code) >= 0.5).ToArray();
        var adjacent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in Interests.All.Where(code => Math.Abs(vector.GetValueOrDefault(code)) < 0.2))
        {
            var near = lifts is not null && travelersInMatrix >= MinTravelersForMatrix
                ? liked.Any(other => other != code && (lifts.GetValueOrDefault((code, other)) > 1.1 || lifts.GetValueOrDefault((other, code)) > 1.1))
                : liked.Any(other => other != code && Interests.LevelOneOf(other) == Interests.LevelOneOf(code));
            if (near)
            {
                adjacent.Add(code);
            }
        }

        return adjacent;
    }

    /// <summary>Number of exploration slots in a list of <paramref name="count"/>: 20 % rounded down, at least one from five places.</summary>
    public static int Slots(int count, double share = 0.2) => count < 5 ? 0 : Math.Max(1, (int)Math.Floor(count * share));

    /// <summary>Dominant level-2 code of a place (the finest category it is mostly about); null when it only has level-1 weights.</summary>
    public static string? DominantLeaf(IReadOnlyDictionary<string, double> weights) => weights
        .Where(pair => pair.Key.Contains('.', StringComparison.Ordinal) && pair.Value > 0d)
        .OrderByDescending(pair => pair.Value)
        .ThenBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => pair.Key)
        .FirstOrDefault();
}
