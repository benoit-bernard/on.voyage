using OnVoyage.Factory.Domain.Classification;
using OnVoyage.Factory.Domain.Geo;
using OnVoyage.Factory.Domain.Scoring;

namespace OnVoyage.Factory.Application;

public enum PlaceStatus
{
    /// <summary>Imported and scored, waiting for an editor.</summary>
    Candidate,

    /// <summary>Classification or data needs a person.</summary>
    NeedsReview,

    Published,
    Rejected,
    Unpublished,

    /// <summary>Folded into another place (reversible, see the dedup link).</summary>
    Merged,
}

/// <summary>A destination Factory can import. The box clips the regional extract to the destination (min longitude, min latitude, max longitude, max latitude).</summary>
public sealed record DestinationConfig(string Slug, string Name, GeoPoint Center, double MinLongitude, double MinLatitude, double MaxLongitude, double MaxLatitude, string OsmExtractUrl, string? OsmExtractFile);

/// <summary>Open data kept apart from the proprietary scores (§7.8): what Wikidata says about the place.</summary>
public sealed record PlaceEnrichment(
    string Qid,
    string? LabelFr,
    string? LabelEn,
    string? DescriptionFr,
    string? DescriptionEn,
    IReadOnlyList<string> InstanceOf,
    IReadOnlyList<string> HeritageStatuses,
    string? Inception,
    int Sitelinks,
    string? WikipediaFr,
    string? WikipediaEn,
    string? Image,
    string? Website,
    DateTimeOffset RetrievedAt);

public sealed record PlaceRecord(
    Guid Id,
    string DestinationSlug,
    string Slug,
    string Name,
    string? NameEn,
    GeoPoint Location,
    Footprint? Footprint,
    string? Qid,
    string OsmType,
    long OsmId,
    IReadOnlyDictionary<string, string> OsmTags,
    PlaceStatus Status,
    PlaceEnrichment? Enrichment,
    long AnnualPageviews,
    int? ImportanceOverride,
    bool EditoriallySaturated,
    int PublishedVersion,
    int? ImportanceScore,
    int? PopularityPercentile,
    bool HiddenGem,
    string? ClassificationOutcome);

public sealed record PlaceScoring(
    Guid PlaceId,
    int Importance,
    int Percentile,
    bool HiddenGem,
    CrowdProfile Crowd,
    TaxonomyVector Vector,
    ClassificationOutcome Outcome,
    double Confidence,
    PlaceStatus Status);

public sealed record DedupLink(Guid Id, Guid KeptPlaceId, Guid OtherPlaceId, string Reason, double Similarity, double DistanceMeters, bool Automatic, DateTimeOffset CreatedAt, DateTimeOffset? RevertedAt);

public sealed record OsmImportResult(string RawTable, int RowCount, string SourceDescription);

public sealed record PlaceDescription(string Name, string? Description, IReadOnlyDictionary<string, string> OsmTags, IReadOnlyList<string> WikidataClasses);

/// <summary>QIDs for the heritage values of Wikidata property P1435 and where a place counts as UNESCO or protected (§7.6). Configurable, see docs.</summary>
public sealed record HeritageClasses(IReadOnlyList<string> Unesco, IReadOnlyList<string> Classified, IReadOnlyList<string> Inscribed)
{
    public static HeritageClasses Defaults { get; } = new(["Q9259"], ["Q10387689"], ["Q10387575"]);

    public HeritageStatus StatusOf(IReadOnlyCollection<string> heritageValues) =>
        heritageValues.Any(value => Classified.Contains(value, StringComparer.OrdinalIgnoreCase)) ? HeritageStatus.Classified
        : heritageValues.Any(value => Inscribed.Contains(value, StringComparer.OrdinalIgnoreCase)) ? HeritageStatus.Inscribed
        : HeritageStatus.None;

    public bool IsUnesco(IReadOnlyCollection<string> heritageValues) => heritageValues.Any(value => Unesco.Contains(value, StringComparer.OrdinalIgnoreCase));
}
