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

internal sealed class SourceDocumentRow
{
    public Guid Id { get; set; }
    public Guid PlaceId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    public string License { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string? Revision { get; set; }
    public DateTimeOffset RetrievedAt { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public double Quality { get; set; }
}

internal sealed class FactRow
{
    public Guid Id { get; set; }
    public Guid PlaceId { get; set; }
    public Guid DocumentId { get; set; }
    public string Statement { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Quote { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string Status { get; set; } = "Validated";
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class StoryRow
{
    public Guid Id { get; set; }
    public Guid PlaceId { get; set; }
    public string Lang { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Status { get; set; } = "Draft";
    public string Title { get; set; } = string.Empty;
    public string Hook { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string RemoteIntro { get; set; } = string.Empty;
    public string AnnounceFront { get; set; } = string.Empty;
    public string AnnounceLeft { get; set; } = string.Empty;
    public string AnnounceRight { get; set; } = string.Empty;
    public string? CareNote { get; set; }
    public Guid[] FactsUsed { get; set; } = [];
    public int EstimatedDurationSeconds { get; set; }
    public string PromptVersion { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public double QualityScore { get; set; }
    public string CheckReport { get; set; } = "{}";
    public string VoiceId { get; set; } = string.Empty;
    public double EditorialScore { get; set; } = 0.8;
    public string? RejectedReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}

internal sealed class StoryAudioPartRow
{
    public Guid StoryId { get; set; }
    public string Part { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public long Bytes { get; set; }
}

internal sealed class StoryReportRow
{
    public Guid Id { get; set; }
    public Guid StoryId { get; set; }
    public Guid TravelerId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = "Open";
    public DateTimeOffset? HandledAt { get; set; }
    public string? Resolution { get; set; }
}

internal sealed class PronunciationRow
{
    public string DestinationSlug { get; set; } = string.Empty;
    public string Term { get; set; } = string.Empty;
    public string Replacement { get; set; } = string.Empty;
}

internal sealed class LlmCallRow
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? PromptId { get; set; }
    public string? PromptVersion { get; set; }
    public Guid? ContentId { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public double CostUsd { get; set; }
    public int DurationMs { get; set; }
    public bool Succeeded { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class AuditLogRow
{
    public Guid Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public int Status { get; set; }
    public string? Detail { get; set; }
}

internal sealed class GenerationBatchRow
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string Criteria { get; set; } = "{}";
    public int Total { get; set; }
}

internal sealed class GenerationJobRow
{
    public Guid Id { get; set; }
    public Guid BatchId { get; set; }
    public Guid PlaceId { get; set; }
    public string PlaceName { get; set; } = string.Empty;
    public string Lang { get; set; } = "fr";
    public string Kind { get; set; } = "Standard";
    public string State { get; set; } = "Pending";
    public string Step { get; set; } = "queued";
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public Guid? StoryId { get; set; }
    public string? Outcome { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class PlaceVideoRow
{
    public Guid PlaceId { get; set; }
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string ThumbnailPath { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTimeOffset SelectedAt { get; set; }
}
