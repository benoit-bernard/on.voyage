using OnVoyage.Taxonomy;

namespace OnVoyage.Recommendation.Engine.Learning;

/// <summary>What the rule of §6.4 produces from a history: the vector <c>u</c>, excluded places, per-place ratings and the profile depth.</summary>
public sealed record LearnedProfile(
    IReadOnlyDictionary<string, double> Vector,
    IReadOnlySet<string> Excluded,
    IReadOnlyDictionary<string, double> Ratings,
    int ProfileDepth,
    DateTimeOffset? LastApplied)
{
    public static LearnedProfile Empty { get; } = new(new Dictionary<string, double>(), new HashSet<string>(), new Dictionary<string, double>(), 0, null);
}

/// <summary>
/// Update rule of §6.4: <c>u[k] ← clamp(u[k] + η·s·p[k]·(1 − |u[k]|), −1, 1)</c> for every unlocked dimension. The rule is not commutative,
/// so the order is (<c>OccurredAt</c>, <c>ClientEventId</c>) and a late event makes the caller replay the whole history with <see cref="Replay"/>.
/// Pure: no clock, no I/O.
/// </summary>
public static class InterestLearning
{
    private static readonly HashSet<string> LevelOneCodes = [.. Interests.LevelOne];

    /// <summary>Signal intensity <c>s</c> of §6.2; null when the kind does not move the vector (impression, direct category choice).</summary>
    public static double? Intensity(Interaction interaction, LearningOptions options) => interaction.Kind switch
    {
        InteractionKinds.OnboardingUp => 1.0,
        InteractionKinds.OnboardingDown => -0.8,
        InteractionKinds.Like => 1.0,
        InteractionKinds.Meh => -0.1,
        InteractionKinds.DislikePoi => options.DislikePoiIntensity,
        InteractionKinds.DislikeCategory => options.DislikeCategoryIntensity,
        InteractionKinds.Listen80 => 0.5,
        InteractionKinds.Replay => 0.6,
        InteractionKinds.AbandonEarly => -0.3,
        InteractionKinds.Save => 0.8,
        InteractionKinds.Navigate => 0.7,
        InteractionKinds.Visit => 0.9 * Math.Clamp(interaction.Value, 0d, 1d),
        InteractionKinds.ExternalLink => 0.4,
        InteractionKinds.CreatorContentOpened => 0.3,
        InteractionKinds.FollowCreator => options.FollowCreatorIntensity,
        _ => null,
    };

    /// <summary>The rating of §6.2 used later by collaborative filtering; −1 means excluded.</summary>
    public static double? Rating(Interaction interaction) => interaction.Kind switch
    {
        InteractionKinds.Like => 1.0,
        InteractionKinds.Meh => -0.2,
        InteractionKinds.DislikePoi => -1.0,
        InteractionKinds.Listen80 => 0.4,
        InteractionKinds.Replay => 0.5,
        InteractionKinds.AbandonEarly => -0.3,
        InteractionKinds.Save => 0.8,
        InteractionKinds.Navigate => 0.6,
        InteractionKinds.Visit => 0.9 * Math.Clamp(interaction.Value, 0d, 1d),
        InteractionKinds.ExternalLink => 0.2,
        InteractionKinds.CreatorContentOpened => 0.2,
        _ => null,
    };

    /// <summary>Level-1 vector <c>c_K</c>: 1 on the node, 0.5 on each child.</summary>
    public static IReadOnlyDictionary<string, double> CategoryVector(string levelOne)
    {
        var vector = new Dictionary<string, double> { [levelOne] = 1d };
        if (Interests.Tree.TryGetValue(levelOne, out var children))
        {
            foreach (var child in children)
            {
                vector[$"{levelOne}.{child}"] = 0.5;
            }
        }

        return vector;
    }

    /// <summary>The level-1 node with the highest value; a level-1 value is the maximum of its own and its children's weights.</summary>
    public static string? DominantCategory(IReadOnlyDictionary<string, double> weights)
    {
        var best = weights
            .GroupBy(pair => Interests.LevelOneOf(pair.Key))
            .Where(group => LevelOneCodes.Contains(group.Key))
            .Select(group => (Code: group.Key, Value: group.Max(pair => pair.Value)))
            .Where(item => item.Value > 0d)
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .FirstOrDefault();
        return best.Code;
    }

    /// <summary>Applies one step to a vector. Locked dimensions (lock end after <paramref name="at"/>) are left alone.</summary>
    public static Dictionary<string, double> Step(
        IReadOnlyDictionary<string, double> vector,
        IReadOnlyDictionary<string, double> weights,
        double intensity,
        double eta,
        IReadOnlyDictionary<string, DateTimeOffset> locks,
        DateTimeOffset at)
    {
        var next = new Dictionary<string, double>(vector);
        foreach (var (code, weight) in weights)
        {
            if (weight <= 0d || locks.TryGetValue(code, out var until) && until > at)
            {
                continue;
            }

            var current = next.GetValueOrDefault(code);
            next[code] = Math.Clamp(current + (eta * intensity * weight * (1d - Math.Abs(current))), -1d, 1d);
        }

        return next;
    }

    /// <summary>Plays the history from zero (plus the <paramref name="pinned"/> manual values), in order. Equivalent to applying the events one by one when none is late.</summary>
    public static LearnedProfile Replay(
        IEnumerable<Interaction> history,
        Func<string, IReadOnlyDictionary<string, double>?> placeWeights,
        IReadOnlyDictionary<string, DateTimeOffset> locks,
        LearningOptions? options = null,
        IReadOnlyDictionary<string, double>? pinned = null)
    {
        options ??= new LearningOptions();

        // A dimension corrected by hand (F-22) starts at its pinned value: replaying from zero must not lose it.
        var vector = new Dictionary<string, double>(pinned ?? new Dictionary<string, double>());
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var ratings = new Dictionary<string, double>(StringComparer.Ordinal);
        var rejections = new List<(DateTimeOffset At, string Category)>();
        var categoryPenaltyAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var seen = new HashSet<Guid>();
        DateTimeOffset? last = null;
        var depth = 0;

        foreach (var interaction in history.OrderBy(i => i.OccurredAt).ThenBy(i => i.ClientEventId))
        {
            if (!seen.Add(interaction.ClientEventId))
            {
                continue;
            }

            last = interaction.OccurredAt;
            depth += DepthPoints(interaction);

            if (interaction.Kind == InteractionKinds.OnboardingCategory && interaction.CategoryCode is { } chosen && LevelOneCodes.Contains(chosen))
            {
                if (!(locks.TryGetValue(chosen, out var until) && until > interaction.OccurredAt))
                {
                    vector[chosen] = interaction.Value >= 0 ? options.CategoryChoice : -options.CategoryChoice;
                }

                continue;
            }

            if (interaction.Kind == InteractionKinds.DislikeCategory)
            {
                if (interaction.CategoryCode is { } category && LevelOneCodes.Contains(category))
                {
                    vector = Step(vector, CategoryVector(category), options.DislikeCategoryIntensity, options.Eta, locks, interaction.OccurredAt);
                }

                continue;
            }

            if (Intensity(interaction, options) is not { } intensity)
            {
                continue;
            }

            var weights = interaction.Kind == InteractionKinds.FollowCreator
                ? interaction.Weights
                : interaction.PoiId is { } poi ? placeWeights(poi) : null;
            if (interaction.PoiId is { } ratedPoi && Rating(interaction) is { } rating)
            {
                ratings[ratedPoi] = rating;
            }

            if (interaction.Kind == InteractionKinds.DislikePoi && interaction.PoiId is { } rejected)
            {
                excluded.Add(rejected);
            }

            if (weights is null)
            {
                continue; // an unknown place moves nothing; the event is still counted and kept
            }

            var eta = interaction.Kind is InteractionKinds.OnboardingUp or InteractionKinds.OnboardingDown ? options.OnboardingEta : options.Eta;
            vector = Step(vector, weights, intensity, eta, locks, interaction.OccurredAt);

            // Three rejections of places of one category within 30 days become one signal on the category (§6.4, step 3).
            if (interaction.Kind == InteractionKinds.DislikePoi && DominantCategory(weights) is { } dominant)
            {
                rejections.Add((interaction.OccurredAt, dominant));
                var recent = rejections.Count(r => r.Category == dominant && interaction.OccurredAt - r.At <= options.RepeatedRejectionsWindow);
                var already = categoryPenaltyAt.TryGetValue(dominant, out var when) && interaction.OccurredAt - when <= options.RepeatedRejectionsWindow;
                if (recent >= options.RepeatedRejections && !already)
                {
                    vector = Step(vector, CategoryVector(dominant), -0.6, options.Eta, locks, interaction.OccurredAt);
                    categoryPenaltyAt[dominant] = interaction.OccurredAt;
                }
            }
        }

        return new LearnedProfile(vector, excluded, ratings, depth, last);
    }

    /// <summary>§6.13: 1 per onboarding answer, 3 per explicit feedback, 1 per listen ≥ 80 %, 2 per save, 3 per probable visit (confidence ≥ 0.7).</summary>
    public static int DepthPoints(Interaction interaction) => interaction.Kind switch
    {
        InteractionKinds.OnboardingUp or InteractionKinds.OnboardingDown or InteractionKinds.OnboardingCategory => 1,
        InteractionKinds.Like or InteractionKinds.Meh or InteractionKinds.DislikePoi or InteractionKinds.DislikeCategory => 3,
        InteractionKinds.Listen80 => 1,
        InteractionKinds.Save => 2,
        InteractionKinds.Visit when interaction.Value >= 0.7 => 3,
        _ => 0,
    };

    /// <summary>Vector written by the onboarding screens (§6.3) from the clips rated and the categories chosen.</summary>
    public static LearnedProfile Onboarding(
        IEnumerable<Interaction> answers,
        Func<string, IReadOnlyDictionary<string, double>?> placeWeights,
        LearningOptions? options = null) =>
        Replay(answers, placeWeights, new Dictionary<string, DateTimeOffset>(), options);
}
