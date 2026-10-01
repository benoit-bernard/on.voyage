namespace OnVoyage.Catalog.Contracts;

public sealed record PoiSummaryDto(
    Guid Id,
    string Slug,
    string Name,
    string Category,
    double Latitude,
    double Longitude,
    double Importance,
    double Quality,
    int CrowdLevel,
    bool HiddenGem,
    int? DistanceMeters,
    int? AudioSeconds,
    IReadOnlyDictionary<string, double> Weights);

public sealed record StoryDto(Guid Id, string Language, string Title, string Text, int DurationSeconds, string? AudioUrl, bool AiGenerated);

/// <summary>A link out (F-19). The app opens it in the system browser or the YouTube app; it never calls YouTube itself, and the thumbnail is our own copy.</summary>
public sealed record LinkDto(string Kind, string Language, string Title, string Url, string? Channel, string? ThumbnailUrl, string? VideoId);

public sealed record PoiDetailDto(
    Guid Id,
    string Slug,
    string Name,
    string Category,
    double Latitude,
    double Longitude,
    double Importance,
    int CrowdLevel,
    bool HiddenGem,
    IReadOnlyList<StoryDto> Stories,
    IReadOnlyList<string> Attributions,
    IReadOnlyList<LinkDto>? Links = null);

public sealed record DestinationDto(string Slug, string Name, double Latitude, double Longitude, int PoiCount);
