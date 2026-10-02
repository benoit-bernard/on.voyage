namespace OnVoyage.Catalog.Contracts;

/// <summary>
/// The Catalog's view of a place changed (§13): published by Catalog after each publication or withdrawal of Factory, to Discovery and Creators.
/// <c>Version</c> is the place's version in the Catalog and only grows; a consumer ignores an event that is not newer.
/// Coordinates are there for the consumers that need them (Discovery); Creators does not keep them.
/// </summary>
public sealed record PoiProjectionChangedV1(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PoiId,
    int Version,
    Guid DestinationId,
    string DestinationSlug,
    string Slug,
    string NameFr,
    string? NameEn,
    IReadOnlyList<string> Aliases,
    string? City,
    double Latitude,
    double Longitude,
    int ImportanceScore,
    bool HiddenGem,
    float ContentQualityScore,
    int CrowdPeak,
    IReadOnlyDictionary<string, float> Weights,
    IReadOnlyList<string> Flags,
    bool IsPublished);
