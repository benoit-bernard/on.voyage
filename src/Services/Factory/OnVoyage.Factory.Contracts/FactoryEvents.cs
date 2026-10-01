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
    bool AccessRegulated);

/// <summary>The place must disappear from the catalog.</summary>
public sealed record PoiUnpublishedV1(Guid EventId, DateTimeOffset OccurredAt, Guid PoiId, int Version, string Reason);
