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

/// <summary>The precomputed collaborative score of one place for one traveler (§6.5): <c>CF</c> in [−1, 1] and the number of neighbours behind it.</summary>
public sealed record CfScoreInfo(double Score, int Support);

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

    /// <summary>The traveler's precomputed collaborative scores (<c>discovery.cf_score</c>) for a destination (all when null); empty until the job has run.</summary>
    Task<IReadOnlyDictionary<Guid, CfScoreInfo>> CfScoresAsync(Guid travelerId, string? destination, CancellationToken cancellationToken);

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

/// <summary>A published creator that validated a place, as seen by the ranking. <c>Commercial</c>: every link to the place is marked "Publicité".</summary>
public sealed record CreatorOnPlaceInfo(Guid CreatorId, string Handle, IReadOnlyDictionary<string, double> Vector, bool Commercial);

public sealed record CreatorInfo(
    Guid CreatorId,
    string Handle,
    string DisplayName,
    string? AvatarPath,
    IReadOnlyList<string> Specialties,
    IReadOnlyDictionary<string, double> Vector,
    int PlaceCount);

/// <summary>Read side of the creators projection: only published creators, only validated links to published places.</summary>
public interface ICreatorReader
{
    /// <summary>For every place of the destination (all places when null): the creators who validated it.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<CreatorOnPlaceInfo>>> OnPlacesAsync(string? destination, CancellationToken cancellationToken);

    /// <summary>Creators with at least one validated place in the destination (all when null), with the number of those places.</summary>
    Task<IReadOnlyList<CreatorInfo>> CreatorsAsync(string? destination, CancellationToken cancellationToken);

    /// <summary>The creators the traveler follows.</summary>
    Task<IReadOnlySet<Guid>> FollowedAsync(Guid travelerId, CancellationToken cancellationToken);
}
