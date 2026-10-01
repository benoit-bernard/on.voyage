using System.Security.Cryptography;
using System.Text;

namespace OnVoyage.Recommendation.Engine.Planning;

/// <summary>A place to schedule: the score is the one of §6.6 for this traveler, <c>Category</c> its dominant level-1 node.</summary>
public sealed record PlannerPlace(string Id, double Latitude, double Longitude, double Score, string? Category);

public sealed record PlannedDay(int Day, IReadOnlyList<PlannerPlace> Places, double MeanScore);

public sealed record PlannerOptions
{
    public int CandidatesPerDay { get; init; } = 6;
    public int MinPerDay { get; init; } = 4;
    public int MaxPerDay { get; init; } = 6;
    public double MaxCategoryShare { get; init; } = 0.4;

    public static double MaxDiameterMeters(TravelMode mode) => mode switch
    {
        TravelMode.Walk => 3_000d,
        TravelMode.Bike => 10_000d,
        _ => 40_000d,
    };
}

/// <summary>
/// "Que visiter ?" (F-11): a list ordered by geography, without opening hours or route optimisation. Deterministic for a given seed
/// (the traveler id). 1) the best <c>J × 6</c> places, with the category ceiling of §6.11; 2) <c>J</c> groups by k-medoids on the great-circle
/// distance, a group never wider than the diameter of the mobility; 3) the 4–6 best of each group, ordered by nearest neighbour from the place
/// closest to the centre; 4) days by decreasing mean score.
/// </summary>
public static class VisitPlanner
{
    public static IReadOnlyList<PlannedDay> Plan(
        IReadOnlyList<PlannerPlace> places,
        int days,
        TravelMode mode,
        (double Latitude, double Longitude) center,
        Guid seed,
        PlannerOptions? options = null)
    {
        options ??= new PlannerOptions();
        days = Math.Clamp(days, 1, 4);
        var ranked = places.OrderByDescending(p => p.Score).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        var wanted = days * options.CandidatesPerDay;
        var diameter = PlannerOptions.MaxDiameterMeters(mode);

        var candidates = Diversify(ranked, wanted, options);
        if (candidates.Count == 0)
        {
            return [];
        }

        var groups = Cluster(candidates, Math.Min(days, candidates.Count), seed);

        // A place beyond the diameter of its group makes room for the next candidate instead.
        var spare = ranked.Where(p => !candidates.Contains(p)).ToList();
        for (var round = 0; round < 10; round++)
        {
            var moved = false;
            foreach (var group in groups)
            {
                while (Diameter(group) > diameter)
                {
                    var worst = group.OrderByDescending(p => TotalDistance(p, group)).ThenBy(p => p.Score).First();
                    group.Remove(worst);
                    moved = true;
                }
            }

            foreach (var next in spare.ToList())
            {
                var home = groups.Where(g => g.Count < options.MaxPerDay && g.All(p => Distance(p, next) <= diameter)).OrderBy(g => g.Min(p => Distance(p, next))).FirstOrDefault();
                if (home is not null)
                {
                    home.Add(next);
                    spare.Remove(next);
                    moved = true;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        var result = groups
            .Where(g => g.Count > 0)
            .Select(group =>
            {
                var best = group.OrderByDescending(p => p.Score).ThenBy(p => p.Id, StringComparer.Ordinal).Take(options.MaxPerDay).ToList();
                var ordered = NearestNeighbour(best, center);
                return (Places: (IReadOnlyList<PlannerPlace>)ordered, Mean: ordered.Average(p => p.Score));
            })
            .OrderByDescending(day => day.Mean)
            .Select((day, index) => new PlannedDay(index + 1, day.Places, day.Mean))
            .ToList();
        return result;
    }

    /// <summary>The best <paramref name="count"/> places with at most <c>max(1, ceil(0.4 · n))</c> from one dominant category (§6.11).</summary>
    public static List<PlannerPlace> Diversify(IReadOnlyList<PlannerPlace> ranked, int count, PlannerOptions options)
    {
        var ceiling = Math.Max(1, (int)Math.Ceiling(options.MaxCategoryShare * count));
        var perCategory = new Dictionary<string, int>(StringComparer.Ordinal);
        var chosen = new List<PlannerPlace>();
        foreach (var place in ranked)
        {
            if (chosen.Count == count)
            {
                break;
            }

            var key = place.Category ?? string.Empty;
            if (key.Length > 0 && perCategory.GetValueOrDefault(key) >= ceiling)
            {
                continue;
            }

            perCategory[key] = perCategory.GetValueOrDefault(key) + 1;
            chosen.Add(place);
        }

        // When the ceiling leaves too few (a small catalogue), fill with the best of what was skipped rather than return a short list.
        foreach (var place in ranked.Where(p => !chosen.Contains(p)))
        {
            if (chosen.Count == count)
            {
                break;
            }

            chosen.Add(place);
        }

        return chosen;
    }

    private static List<List<PlannerPlace>> Cluster(List<PlannerPlace> items, int k, Guid seed)
    {
        // Seeded farthest-first start: the first medoid is picked from the seed, the next ones are the places farthest from those.
        var random = new Random(SeedOf(seed));
        var medoids = new List<PlannerPlace> { items[random.Next(items.Count)] };
        while (medoids.Count < k)
        {
            medoids.Add(items.Where(p => !medoids.Contains(p)).OrderByDescending(p => medoids.Min(m => Distance(p, m))).ThenBy(p => p.Id, StringComparer.Ordinal).First());
        }

        List<List<PlannerPlace>> groups = [];
        for (var iteration = 0; iteration < 50; iteration++)
        {
            groups = [.. medoids.Select(_ => new List<PlannerPlace>())];
            foreach (var item in items)
            {
                var nearest = Enumerable.Range(0, k).OrderBy(i => Distance(item, medoids[i])).ThenBy(i => i).First();
                groups[nearest].Add(item);
            }

            var next = groups.Select((g, i) => g.Count == 0 ? medoids[i] : g.OrderBy(c => g.Sum(o => Distance(c, o))).ThenBy(c => c.Id, StringComparer.Ordinal).First()).ToList();
            if (next.SequenceEqual(medoids))
            {
                break;
            }

            medoids = next;
        }

        return groups;
    }

    private static List<PlannerPlace> NearestNeighbour(List<PlannerPlace> group, (double Latitude, double Longitude) center)
    {
        var remaining = new List<PlannerPlace>(group);
        var ordered = new List<PlannerPlace>();
        var current = remaining.OrderBy(p => Distance(p.Latitude, p.Longitude, center.Latitude, center.Longitude)).ThenBy(p => p.Id, StringComparer.Ordinal).First();
        while (true)
        {
            ordered.Add(current);
            remaining.Remove(current);
            if (remaining.Count == 0)
            {
                return ordered;
            }

            var from = current;
            current = remaining.OrderBy(p => Distance(p, from)).ThenBy(p => p.Id, StringComparer.Ordinal).First();
        }
    }

    /// <summary>Length of the walk through the places in the given order, in metres.</summary>
    public static double PathLength(IReadOnlyList<PlannerPlace> ordered)
    {
        var total = 0d;
        for (var i = 1; i < ordered.Count; i++)
        {
            total += Distance(ordered[i - 1], ordered[i]);
        }

        return total;
    }

    private static double Diameter(List<PlannerPlace> group)
    {
        var widest = 0d;
        for (var i = 0; i < group.Count; i++)
        {
            for (var j = i + 1; j < group.Count; j++)
            {
                widest = Math.Max(widest, Distance(group[i], group[j]));
            }
        }

        return widest;
    }

    private static double TotalDistance(PlannerPlace place, List<PlannerPlace> group) => group.Sum(other => Distance(place, other));

    private static int SeedOf(Guid seed) => BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString("N"))), 0);

    private static double Distance(PlannerPlace a, PlannerPlace b) => Distance(a.Latitude, a.Longitude, b.Latitude, b.Longitude);

    public static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_008.8;
        var dLat = (lat2 - lat1) * Math.PI / 180d;
        var dLon = (lon2 - lon1) * Math.PI / 180d;
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2)) + (Math.Cos(lat1 * Math.PI / 180d) * Math.Cos(lat2 * Math.PI / 180d) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}
