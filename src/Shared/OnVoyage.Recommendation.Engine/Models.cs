namespace OnVoyage.Recommendation.Engine;

/// <summary>Traveler affinity <c>u</c>, each dimension in [-1, 1]; absent dimension means unknown (0).</summary>
public sealed record TasteProfile(IReadOnlyDictionary<string, double> Affinities, int Depth)
{
    public static TasteProfile Empty { get; } = new(new Dictionary<string, double>(), 0);

    public double this[string code] => Affinities.TryGetValue(code, out var value) ? value : 0d;
}

/// <summary>A place being ranked. <c>Weights</c> is the place vector <c>p</c>, each dimension in [0, 1].</summary>
public sealed record Candidate(
    string Id,
    IReadOnlyDictionary<string, double> Weights,
    double Importance,
    double Quality,
    double? DistanceMeters,
    int CrowdLevel = 1,
    bool HiddenGem = false,
    int Impressions7d = 0);

public enum TravelMode { Walk, Bike, Car }

public enum EthicalLevel { Off, Balanced, Strong }

public enum ReasonCode { ColdStart, Categories, HiddenGem }

public sealed record Reason(ReasonCode Code, IReadOnlyList<string> Categories);

public sealed record ScoredCandidate(Candidate Candidate, double Score, int? CompatibilityPercent, Reason Reason);
