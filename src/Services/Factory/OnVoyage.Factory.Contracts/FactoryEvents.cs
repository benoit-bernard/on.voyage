namespace OnVoyage.Factory.Contracts;

// Integration events published by Factory (cahier des charges §13). Immutable, versioned by name, consumed idempotently by version.

public sealed record PoiInterestV1(string TaxonomyCode, float Weight);

public sealed record PoiCrowdProfileV1(int Offpeak, int Shoulder, int Peak);

public sealed record PoiDestinationV1(string Slug, string Name, double Latitude, double Longitude);

/// <summary>A place is available to travelers (or changed). <c>Version</c> only grows; a consumer ignores an event that is not newer.</summary>
public sealed record PoiPublishedV1(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PoiId,
    int Version,
    PoiDestinationV1 Destination,
    string Slug,
    string NameFr,
    string? NameEn,
    double Latitude,
    double Longitude,
    int ImportanceScore,
    int PopularityPercentile,
    bool HiddenGem,
    float ContentQualityScore,
    int TaxonomyVersion,
    IReadOnlyList<PoiInterestV1> Interests,
    PoiCrowdProfileV1 Crowd,
    bool Fragile,
    bool AccessRegulated,
    IReadOnlyList<PoiLinkV1>? Links = null);

/// <summary>A link out (F-19): an encyclopedia article, an official site or a video an editor picked. Thumbnails live in our own storage.</summary>
public sealed record PoiLinkV1(string Kind, string Lang, string Url, string Title, string? Channel = null, string? ThumbnailPath = null, string? VideoId = null);

/// <summary>The place must disappear from the catalog.</summary>
public sealed record PoiUnpublishedV1(Guid EventId, DateTimeOffset OccurredAt, Guid PoiId, int Version, string Reason);

public sealed record StoryAudioPartV1(string Part, string Path, string Sha256, int DurationSeconds);

public sealed record StorySourceV1(string Title, string? Publisher, string Url, string License);

/// <summary>A story is available (§13). Texts are empty for Premium stories, which Factory does not produce yet.</summary>
public sealed record StoryPublishedV1(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid StoryId,
    Guid PoiId,
    string Lang,
    string Kind,
    int Version,
    string Title,
    string Hook,
    string Text,
    string RemoteIntro,
    int DurationSeconds,
    string VoiceId,
    bool IsAiGenerated,
    bool IsPremium,
    IReadOnlyList<StoryAudioPartV1> AudioParts,
    IReadOnlyList<StorySourceV1> Sources);

public sealed record StoryUnpublishedV1(Guid EventId, DateTimeOffset OccurredAt, Guid StoryId, Guid PoiId, int Version, string Reason);

public sealed record StoryArchivedV1(Guid EventId, DateTimeOffset OccurredAt, Guid StoryId, Guid PoiId, int Version);
