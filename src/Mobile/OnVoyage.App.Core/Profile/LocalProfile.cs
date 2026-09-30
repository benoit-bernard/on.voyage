namespace OnVoyage.App.Core.Profile;

/// <summary>Taste profile stored on the device only (privacy by design). Never sent to a server in MVP-0.</summary>
public sealed record LocalProfile
{
    public Guid TravelerId { get; init; } = Guid.CreateVersion7();

    public Dictionary<string, double> Affinities { get; init; } = [];

    public int Depth { get; init; }

    public HashSet<Guid> Saved { get; init; } = [];

    public bool OnboardingDone { get; init; }

    public string Destination { get; init; } = "marseille";
}
