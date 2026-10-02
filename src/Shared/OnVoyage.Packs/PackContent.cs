namespace OnVoyage.Packs;

/// <summary>A published story of a place. Each <see cref="PackAudio.SourcePath"/> is a file the builder can read.</summary>
public sealed record PackStory(Guid Id, int Version, string Kind, string Title, string Text, string RemoteIntro, int DurationSeconds, bool AiGenerated, IReadOnlyList<string> Sources, IReadOnlyList<PackAudio> Audio);

public sealed record PackAudio(string Part, string SourcePath, int DurationSeconds);

public sealed record PackPlace(
    Guid Id,
    string Slug,
    string Name,
    string Category,
    double Latitude,
    double Longitude,
    int Importance,
    int CrowdLevel,
    bool Fragile,
    bool AccessRegulated,
    bool HiddenGem,
    double Quality,
    bool CarAccessible,
    bool VisibleFromRoad,
    string? Summary,
    IReadOnlyDictionary<string, double> Interests,
    IReadOnlyList<PackStory> Stories);

/// <summary>Everything a pack is made of. <see cref="MapPath"/> is the PMTiles extract of the destination, when one was made.</summary>
public sealed record PackContent(string Destination, string Lang, int Version, string? TaxonomyVersion, string? MinAppVersion, IReadOnlyList<PackPlace> Places, string? MapPath = null);
