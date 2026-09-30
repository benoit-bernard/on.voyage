using OnVoyage.Taxonomy;

namespace OnVoyage.App.Core.Profile;

/// <summary>Profile update rules of §6.3–6.4 (MVP-0 subset: level-1 onboarding, likes and saves on places).</summary>
public static class ProfileUpdater
{
    public const double CategorySeed = 0.6;
    public const double Eta = 0.15;
    public const double SaveSignal = 0.8;
    public const double LikeSignal = 1.0;
    public const double DislikeSignal = -0.6;

    /// <summary>Onboarding chips: liked level-1 categories get <c>+0.6</c>, disliked ones <c>-0.6</c>; depth counts once per choice.</summary>
    public static LocalProfile ApplyOnboarding(LocalProfile profile, IReadOnlyCollection<string> liked, IReadOnlyCollection<string> disliked)
    {
        var affinities = new Dictionary<string, double>(profile.Affinities);
        foreach (var code in liked.Where(Interests.LevelOne.Contains))
        {
            affinities[code] = CategorySeed;
        }

        foreach (var code in disliked.Where(Interests.LevelOne.Contains))
        {
            affinities[code] = -CategorySeed;
        }

        return profile with
        {
            Affinities = affinities,
            Depth = profile.Depth + liked.Count + disliked.Count,
            OnboardingDone = true,
        };
    }

    /// <summary>Moves the affinity of every dimension of the place toward the signal: <c>u[k] += eta · p[k] · (signal − u[k])</c>, clamped to [-1, 1].</summary>
    public static LocalProfile ApplySignal(LocalProfile profile, IReadOnlyDictionary<string, double> placeWeights, double signal)
    {
        var affinities = new Dictionary<string, double>(profile.Affinities);
        foreach (var (code, weight) in placeWeights)
        {
            var current = affinities.GetValueOrDefault(code);
            affinities[code] = Math.Clamp(current + (Eta * weight * (signal - current)), -1d, 1d);
        }

        return profile with { Affinities = affinities, Depth = profile.Depth + 1 };
    }

    public static LocalProfile ToggleSaved(LocalProfile profile, Guid placeId, IReadOnlyDictionary<string, double> placeWeights)
    {
        var saved = new HashSet<Guid>(profile.Saved);
        if (!saved.Remove(placeId))
        {
            saved.Add(placeId);
            return ApplySignal(profile with { Saved = saved }, placeWeights, SaveSignal);
        }

        return profile with { Saved = saved };
    }
}
