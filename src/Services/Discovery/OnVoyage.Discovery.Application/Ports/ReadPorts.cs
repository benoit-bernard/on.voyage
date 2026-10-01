namespace OnVoyage.Discovery.Application.Ports;

public sealed record PlaceInfo(
    Guid PoiId,
    string Slug,
    string Name,
    string Destination,
    double Latitude,
    double Longitude,
    IReadOnlyDictionary<string, double> Weights,
    double Importance,
    double Quality,
    bool HiddenGem,
    bool Fragile,
    bool AccessRegulated,
    int CrowdLevel);

public sealed record StoryInfo(Guid StoryId, Guid PoiId, string Lang, string Kind, int DurationSeconds, bool IsPremium, IReadOnlyDictionary<string, string> AudioParts);

public sealed record TravelerInfo(Guid Id, string Lang, string EthicalMode, bool IsPremium, int ProfileDepth, string Cohort);

public sealed record HistoryEntry(Guid PoiId, DateTimeOffset LastAt, bool Listened, bool Visited);

/// <summary>Read side over the projection of published places and stories.</summary>
public interface IPlaceReader
{
    Task<IReadOnlyList<PlaceInfo>> PlacesAsync(string? destination, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoryInfo>> StoriesAsync(string lang, CancellationToken cancellationToken);

    /// <summary>Impressions of the last seven days per place, all travelers together (the <c>Novelty</c> term).</summary>
    Task<IReadOnlyDictionary<Guid, int>> ImpressionsSinceAsync(DateTimeOffset since, CancellationToken cancellationToken);

    Task<(string Slug, string Name, double Latitude, double Longitude)?> DestinationAsync(string slug, CancellationToken cancellationToken);
}

public interface ITravelerReader
{
    Task<TravelerInfo?> GetAsync(Guid travelerId, CancellationToken cancellationToken);

    /// <summary>Ratings of §6.2 by place (−1 … 1).</summary>
    Task<IReadOnlyDictionary<Guid, double>> RatingsAsync(Guid travelerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<(Guid PoiId, DateTimeOffset SavedAt)>> SavedAsync(Guid travelerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<HistoryEntry>> HistoryAsync(Guid travelerId, CancellationToken cancellationToken);

    /// <summary>The last places proposed by "Surprenez-moi", newest first.</summary>
    Task<IReadOnlyList<Guid>> RecentSurprisesAsync(Guid travelerId, int count, CancellationToken cancellationToken);
}

/// <summary>Global co-appreciation matrix of §6.10, recomputed every night.</summary>
public interface IAffinityStore
{
    Task<(IReadOnlyDictionary<(string A, string B), double> Lifts, int Travelers)> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Recomputes the matrix from the vectors of travelers with a rich enough profile; returns how many travelers were used.</summary>
    Task<int> RecomputeAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
