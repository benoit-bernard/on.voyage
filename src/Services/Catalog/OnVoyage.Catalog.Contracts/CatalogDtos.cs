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
    IReadOnlyList<string> Attributions);

public sealed record DestinationDto(string Slug, string Name, double Latitude, double Longitude, int PoiCount);
