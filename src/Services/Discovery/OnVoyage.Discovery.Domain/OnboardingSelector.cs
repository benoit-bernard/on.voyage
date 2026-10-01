using OnVoyage.Recommendation.Engine.Learning;

namespace OnVoyage.Discovery.Domain;

/// <summary>A published onboarding clip with what the selection needs to know about its place.</summary>
public sealed record ClipCandidate(Guid StoryId, Guid PoiId, string Lang, IReadOnlyDictionary<string, double> Weights, double Importance, double Quality);

/// <summary>
/// Picks the five clips of screen 1 (F-02): level-1 categories that are all different, so the answers say as much as possible about
/// the traveler. Per category the best clip wins (quality, then importance); the five categories with the best clips are kept.
/// Deterministic: the same candidates always give the same five.
/// </summary>
public static class OnboardingSelector
{
    public const int ClipCount = 5;

    public static IReadOnlyList<ClipCandidate> Select(IEnumerable<ClipCandidate> candidates, int count = ClipCount) =>
        [.. candidates
            .Select(clip => (Clip: clip, Category: InterestLearning.DominantCategory(clip.Weights)))
            .Where(item => item.Category is not null)
            .GroupBy(item => item.Category!)
            .Select(group => group
                .OrderByDescending(item => item.Clip.Quality)
                .ThenByDescending(item => item.Clip.Importance)
                .ThenBy(item => item.Clip.StoryId)
                .First())
            .OrderByDescending(item => item.Clip.Quality)
            .ThenByDescending(item => item.Clip.Importance)
            .ThenBy(item => item.Category, StringComparer.Ordinal)
            .Take(count)
            .Select(item => item.Clip)];

    /// <summary>True when every clip has a different dominant level-1 category.</summary>
    public static bool AreDistinct(IEnumerable<ClipCandidate> clips)
    {
        var categories = clips.Select(clip => InterestLearning.DominantCategory(clip.Weights)).ToArray();
        return categories.All(category => category is not null) && categories.Distinct(StringComparer.Ordinal).Count() == categories.Length;
    }
}
