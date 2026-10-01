using OnVoyage.Discovery.Domain;
using OnVoyage.Recommendation.Engine.Learning;

namespace OnVoyage.Discovery.Application.Ports;

/// <summary>An interaction as received, with the fields that are kept besides what the learning rule needs.</summary>
public sealed record IncomingInteraction(Interaction Interaction, Guid? StoryId, int? StoryVersion, int? DwellSeconds, string? Surface);

public sealed record Locks(IReadOnlyDictionary<string, DateTimeOffset> Until, IReadOnlyDictionary<string, double> Pinned)
{
    public static Locks None { get; } = new(new Dictionary<string, DateTimeOffset>(), new Dictionary<string, double>());
}

public sealed record StoredProfile(LearnedProfile Learned, Locks Locks, string Cohort, int TaxonomyVersion);

/// <summary>Everything done for one traveler while the traveler's lock is held, so two batches never interleave.</summary>
public interface ITravelerSession
{
    /// <summary>Stores the interactions that are new (by <c>client_event_id</c>), returns how many were.</summary>
    Task<int> AddAsync(IReadOnlyList<IncomingInteraction> items, CancellationToken cancellationToken);

    /// <summary>The learning history in replay order; impressions are not part of it.</summary>
    Task<IReadOnlyList<Interaction>> HistoryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>> PlaceWeightsAsync(IEnumerable<string> poiIds, CancellationToken cancellationToken);

    Task<Locks> LocksAsync(CancellationToken cancellationToken);

    Task SaveAsync(LearnedProfile learned, Locks locks, CancellationToken cancellationToken);
}

public interface IDiscoveryStore
{
    /// <summary>Creates the traveler on first use, takes the traveler's lock, runs the work in one transaction.</summary>
    Task<T> ExclusiveAsync<T>(Guid travelerId, Func<ITravelerSession, Task<T>> work, CancellationToken cancellationToken);

    Task<StoredProfile?> GetProfileAsync(Guid travelerId, CancellationToken cancellationToken);
}

public sealed record StoredClip(Guid StoryId, Guid PoiId, string Lang, string Title, string AudioPath, int DurationSeconds, bool Active, ClipCandidate Candidate);

public interface IOnboardingStore
{
    Task<IReadOnlyList<StoredClip>> ListAsync(string? lang, CancellationToken cancellationToken);

    /// <summary>Replaces the active selection. Returns false when a story is not an onboarding clip.</summary>
    Task<bool> SetActiveAsync(IReadOnlyList<Guid> storyIds, CancellationToken cancellationToken);
}

public sealed record PlaceProjection(Guid PoiId, string Slug, string Destination, IReadOnlyDictionary<string, double> Weights, double Importance, double Quality, bool HiddenGem, bool Fragile, bool IsPublished, int Version);

public sealed record ClipProjection(Guid StoryId, Guid PoiId, string Lang, string Title, string AudioPath, int DurationSeconds, int Version);

public interface IProjectionWriter
{
    Task ApplyPlaceAsync(PlaceProjection place, CancellationToken cancellationToken);

    Task UnpublishPlaceAsync(Guid poiId, int version, CancellationToken cancellationToken);

    Task ApplyClipAsync(ClipProjection clip, CancellationToken cancellationToken);

    Task RemoveClipAsync(Guid storyId, CancellationToken cancellationToken);
}

/// <summary>Public base URL of the media files, so the app can play a clip without going through the Catalog.</summary>
public interface IMediaUrls
{
    string Url(string path);
}
