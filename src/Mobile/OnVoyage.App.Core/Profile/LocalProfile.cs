namespace OnVoyage.App.Core.Profile;

/// <summary>Taste profile stored on the device only (privacy by design). Never sent to a server in MVP-0.</summary>
public sealed record LocalProfile
{
    public Guid TravelerId { get; init; } = Guid.CreateVersion7();

    public Dictionary<string, double> Affinities { get; init; } = [];

    public int Depth { get; init; }

    public HashSet<Guid> Saved { get; init; } = [];

    /// <summary>When each place was saved: the reminder mentions the year of an old save (F-08).</summary>
    public Dictionary<Guid, DateTimeOffset> SavedAt { get; init; } = [];

    /// <summary>Places the traveler turned down ("Pas pour moi — ce lieu"): never suggested, never told by the discovery mode (F-07).</summary>
    public HashSet<Guid> Excluded { get; init; } = [];

    public bool OnboardingDone { get; init; }

    /// <summary>The five onboarding clips as last received, so the first screen works without a network (F-02).</summary>
    public string? OnboardingClipsJson { get; init; }

    /// <summary>Answers not yet accepted by Discovery (offline at the end of the onboarding); sent again later, idempotently.</summary>
    public string? PendingOnboardingJson { get; init; }

    public string Destination { get; init; } = "marseille";

    /// <summary>"Privilégier les lieux moins fréquentés" (F-13): <c>off</c>, <c>balanced</c> or <c>strong</c>.</summary>
    public string EthicalMode { get; init; } = "balanced";

    /// <summary>The weights of §6.6 for this traveler's ethical setting.</summary>
    public Recommendation.Engine.RecommendationOptions Options() => new()
    {
        Ethical = EthicalMode switch { "off" => Recommendation.Engine.EthicalLevel.Off, "strong" => Recommendation.Engine.EthicalLevel.Strong, _ => Recommendation.Engine.EthicalLevel.Balanced },
    };
}
