using NetTopologySuite.Geometries;

namespace OnVoyage.Factory.Infrastructure.Persistence;

// Open data (OSM, Wikidata, Wikipedia page views) lives in factory_raw; scores, status and editorial fields in factory (§7.8).

internal sealed class ImportRunRow
{
    public Guid Id { get; set; }
    public string DestinationSlug { get; set; } = string.Empty;
    public string RawTable { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int RawRows { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public DateTimeOffset StartedAt { get; set; }
}

internal sealed class PlaceRow
{
    public Guid Id { get; set; }
    public string DestinationSlug { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? NameEn { get; set; }
    public Point Location { get; set; } = null!;
    public MultiPolygon? Footprint { get; set; }
    public string? Qid { get; set; }
    public string OsmType { get; set; } = string.Empty;
    public long OsmId { get; set; }
    public int? OsmVersion { get; set; }
    public string OsmTags { get; set; } = "{}";
    public string Status { get; set; } = "Candidate";
    public long AnnualPageviews { get; set; }
    public short? ImportanceScore { get; set; }
    public short? PopularityPercentile { get; set; }
    public bool HiddenGem { get; set; }
    public short CrowdOffpeak { get; set; } = 1;
    public short CrowdShoulder { get; set; } = 1;
    public short CrowdPeak { get; set; } = 1;
    public string? ClassificationOutcome { get; set; }
    public float ClassificationConfidence { get; set; }
    public short? ImportanceOverride { get; set; }
    public bool EditoriallySaturated { get; set; }
    public bool Fragile { get; set; }
    public bool AccessRegulated { get; set; }
    public Guid? MergedInto { get; set; }
    public int PublishedVersion { get; set; }
    public string Source { get; set; } = "osm";
    public string SourceLicense { get; set; } = "ODbL-1.0";
    public string SourceUrl { get; set; } = string.Empty;
    public Guid ImportRunId { get; set; }
    public DateTimeOffset RetrievedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<PlaceInterestRow> Interests { get; set; } = [];
}

internal sealed class PlaceInterestRow
{
    public Guid PlaceId { get; set; }
    public string TaxonomyCode { get; set; } = string.Empty;
    public float Weight { get; set; }

    /// <summary><c>rule</c>, <c>model</c> or <c>editor</c>.</summary>
    public string Source { get; set; } = "rule";
}

internal sealed class DedupLinkRow
{
    public Guid Id { get; set; }
    public Guid KeptPlaceId { get; set; }
    public Guid OtherPlaceId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public double Similarity { get; set; }
    public double DistanceMeters { get; set; }
    public bool Automatic { get; set; }

    /// <summary>The Wikidata item the kept place took from the other one, so a revert can give it back.</summary>
    public string? InheritedQid { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevertedAt { get; set; }

    /// <summary><c>proposed</c> until a person confirms it, then <c>merged</c>.</summary>
    public string State { get; set; } = "proposed";
}

internal sealed class WikidataEntityRow
{
    public string Qid { get; set; } = string.Empty;
    public string? LabelFr { get; set; }
    public string? LabelEn { get; set; }
    public string? DescriptionFr { get; set; }
    public string? DescriptionEn { get; set; }
    public string[] InstanceOf { get; set; } = [];
    public string[] HeritageStatuses { get; set; } = [];
    public string? Inception { get; set; }
    public int Sitelinks { get; set; }
    public string? WikipediaFr { get; set; }
    public string? WikipediaEn { get; set; }
    public string? Image { get; set; }
    public string? Website { get; set; }
    public string Source { get; set; } = "wikidata";
    public string SourceLicense { get; set; } = "CC0-1.0";
    public string SourceUrl { get; set; } = string.Empty;
    public DateTimeOffset RetrievedAt { get; set; }
}

internal sealed class PageviewsRow
{
    public string Qid { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public long Views12Months { get; set; }
    public string SourceLicense { get; set; } = "CC0-1.0";
    public DateTimeOffset RetrievedAt { get; set; }
}
