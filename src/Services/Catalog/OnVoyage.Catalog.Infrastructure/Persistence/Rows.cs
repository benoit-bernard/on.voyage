using NetTopologySuite.Geometries;

namespace OnVoyage.Catalog.Infrastructure.Persistence;

// Persistence models (cahier des charges §11.1). Distinct from the Domain entities; mapped explicitly in PoiMapper.

internal sealed class DestinationRow
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string NameFr { get; set; } = string.Empty;
    public string? NameEn { get; set; }
    public Point Center { get; set; } = null!;
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<PoiRow> Pois { get; set; } = [];
}

internal sealed class TaxonomyNodeRow
{
    public string Code { get; set; } = string.Empty;
    public string? ParentCode { get; set; }
    public short Level { get; set; }
    public int TaxonomyVersion { get; set; }
    public int SortOrder { get; set; }
}

internal sealed class PoiRow
{
    public Guid Id { get; set; }
    public Guid DestinationId { get; set; }
    public DestinationRow Destination { get; set; } = null!;
    public string Slug { get; set; } = string.Empty;
    public Point Location { get; set; } = null!;
    public short ImportanceScore { get; set; }
    public bool HiddenGem { get; set; }
    public float ContentQualityScore { get; set; }
    public int TaxonomyVersion { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public List<PoiTextRow> Texts { get; set; } = [];
    public List<PoiInterestRow> Interests { get; set; } = [];
    public PoiEthicsRow? Ethics { get; set; }
    public List<StoryRow> Stories { get; set; } = [];
}

internal sealed class PoiTextRow
{
    public Guid PoiId { get; set; }
    public string Lang { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ShortDescription { get; set; }
    public NpgsqlTypes.NpgsqlTsVector SearchVector { get; set; } = null!;
}

internal sealed class PoiInterestRow
{
    public Guid PoiId { get; set; }
    public string TaxonomyCode { get; set; } = string.Empty;
    public float Weight { get; set; }
}

internal sealed class CrowdProfileJson
{
    public short Offpeak { get; set; }
    public short Shoulder { get; set; }
    public short Peak { get; set; }
}

internal sealed class PoiEthicsRow
{
    public Guid PoiId { get; set; }
    public CrowdProfileJson CrowdProfile { get; set; } = new();
    public bool Fragile { get; set; }
    public bool AccessRegulated { get; set; }
}

internal sealed class StoryRow
{
    public Guid Id { get; set; }
    public Guid PoiId { get; set; }
    public string Lang { get; set; } = string.Empty;
    public string Kind { get; set; } = "standard";
    public int Version { get; set; } = 1;
    public string Title { get; set; } = string.Empty;
    public string? Text { get; set; }
    public int DurationSeconds { get; set; }
    public string? AudioPath { get; set; }
    public bool IsPremium { get; set; }
    public bool IsAiGenerated { get; set; }
    public string Status { get; set; } = "published";
    public DateTimeOffset? PublishedAt { get; set; }
}
