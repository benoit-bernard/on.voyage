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

    public string Destination { get; init; } = "marseille";
}
