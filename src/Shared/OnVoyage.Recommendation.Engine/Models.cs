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
    int Impressions7d = 0,
    CreatorEndorsement? Creator = null,
    CollaborativeSignal? Collaborative = null);

public enum TravelMode { Walk, Bike, Car }

public enum EthicalLevel { Off, Balanced, Strong }

public enum ReasonCode { ColdStart, Categories, HiddenGem, CreatorFollowed, CreatorSimilar, Collaborative }

/// <summary><paramref name="Creator"/> is the handle of the creator behind <see cref="ReasonCode.CreatorFollowed"/> and <see cref="ReasonCode.CreatorSimilar"/>.</summary>
public sealed record Reason(ReasonCode Code, IReadOnlyList<string> Categories, string? Creator = null);

/// <summary>
/// What the neighbours of §6.5 say about a place, precomputed: <see cref="Score"/> is <c>CF</c> in [-1, 1] and <see cref="Support"/> the number of
/// neighbours who rated it. Never carries a neighbour's identity.
/// </summary>
public sealed record CollaborativeSignal(double Score, int Support);

/// <param name="CollaborativeContribution"><c>w_cf_effectif · CF01</c>, the part of <paramref name="Score"/> that comes from the neighbours (§6.9).</param>
public sealed record ScoredCandidate(Candidate Candidate, double Score, int? CompatibilityPercent, Reason Reason, double CollaborativeContribution = 0d);
