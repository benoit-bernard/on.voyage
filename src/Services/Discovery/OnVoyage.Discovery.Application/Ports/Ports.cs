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

    /// <summary>Adds or removes a place from the traveler's wishes (idempotent).</summary>
    Task SetWishAsync(Guid poiId, bool saved, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Deletes the interactions, visits, impressions and rating of one place (F-22: "Mon historique").</summary>
    Task DeletePlaceAsync(Guid poiId, CancellationToken cancellationToken);

    /// <summary>Records that "Surprenez-moi" proposed this place (kept for the "not the last 20" rule).</summary>
    Task RecordSurpriseAsync(Guid poiId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Records that the traveler follows or stops following a creator. False when a newer change is already stored (events arrive in any order);
    /// the stored state is then left alone.
    /// </summary>
    Task<bool> SetFollowAsync(Guid creatorId, bool following, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The creator vector <c>c</c> as projected now; empty when the creator is unknown or has no validated place.</summary>
    Task<IReadOnlyDictionary<string, double>> CreatorVectorAsync(Guid creatorId, CancellationToken cancellationToken);
}

public interface IDiscoveryStore
{
    /// <summary>Creates the traveler on first use, takes the traveler's lock, runs the work in one transaction.</summary>
    Task<T> ExclusiveAsync<T>(Guid travelerId, Func<ITravelerSession, Task<T>> work, CancellationToken cancellationToken);

    Task<StoredProfile?> GetProfileAsync(Guid travelerId, CancellationToken cancellationToken);

    /// <summary>Updates language and ethical mode, creating the traveler if needed; returns the settings after the change.</summary>
    Task<(string Lang, string EthicalMode)> UpdateSettingsAsync(Guid travelerId, string? lang, string? ethicalMode, CancellationToken cancellationToken);
}

public sealed record StoredClip(Guid StoryId, Guid PoiId, string Lang, string Title, string AudioPath, int DurationSeconds, bool Active, ClipCandidate Candidate);

public interface IOnboardingStore
{
    Task<IReadOnlyList<StoredClip>> ListAsync(string? lang, CancellationToken cancellationToken);

    /// <summary>Replaces the active selection. Returns false when a story is not an onboarding clip.</summary>
    Task<bool> SetActiveAsync(IReadOnlyList<Guid> storyIds, CancellationToken cancellationToken);
}

public sealed record PlaceProjection(Guid PoiId, string Slug, string Name, string Destination, double Latitude, double Longitude, int CrowdLevel, bool AccessRegulated, IReadOnlyDictionary<string, double> Weights, double Importance, double Quality, bool HiddenGem, bool Fragile, bool IsPublished, int Version);

public sealed record ClipProjection(Guid StoryId, Guid PoiId, string Lang, string Title, string AudioPath, int DurationSeconds, int Version);

public sealed record StoryProjection(Guid StoryId, Guid PoiId, string Lang, string Kind, int DurationSeconds, bool IsPremium, IReadOnlyDictionary<string, string> AudioParts, int Version);

public interface IProjectionWriter
{
    Task ApplyStoryAsync(StoryProjection story, CancellationToken cancellationToken);

    Task RemoveStoryAsync(Guid storyId, CancellationToken cancellationToken);

    Task ApplyPlaceAsync(PlaceProjection place, CancellationToken cancellationToken);

    Task UnpublishPlaceAsync(Guid poiId, int version, CancellationToken cancellationToken);

    Task ApplyClipAsync(ClipProjection clip, CancellationToken cancellationToken);

    Task RemoveClipAsync(Guid storyId, CancellationToken cancellationToken);
}

public sealed record CreatorProjection(Guid CreatorId, string Handle, string DisplayName, string? AvatarPath, IReadOnlyList<string> Specialties, bool IsPublished, DateTimeOffset OccurredAt);

/// <summary>One link of a creator to a place; <c>ContentId</c> is null for a tip alone. <c>Validated</c> false means removed.</summary>
public sealed record CreatorLinkProjection(Guid CreatorId, Guid PoiId, Guid? ContentId, string Kind, bool IsCommercial, bool Validated, DateTimeOffset OccurredAt);

/// <summary>Write side of the creators projection (T-1205). Every method keeps the newest event by <c>OccurredAt</c>, so redelivery and reordering change nothing.</summary>
public interface ICreatorProjectionWriter
{
    /// <summary>Creates or updates a creator (published, or unpublished with the reason dropped) and refreshes its vector.</summary>
    Task ApplyCreatorAsync(CreatorProjection creator, CancellationToken cancellationToken);

    /// <summary>Stores a validated or removed link and refreshes the vector of the creator.</summary>
    Task ApplyLinkAsync(CreatorLinkProjection link, CancellationToken cancellationToken);
}

/// <summary>Public base URL of the media files, so the app can play a clip without going through the Catalog.</summary>
public interface IMediaUrls
{
    string Url(string path);
}
