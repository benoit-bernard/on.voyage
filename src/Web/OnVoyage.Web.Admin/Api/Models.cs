using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnVoyage.Web.Admin.Api;

// What the back-office APIs return (camelCase JSON, enums as text). Kept as plain records: the admin reads, it does not share code with the services.

public sealed record DestinationItem(string Slug, string Name, double Latitude, double Longitude);

public sealed record PlaceItem(
    Guid Id, string Slug, string Name, string? NameEn, double Latitude, double Longitude, string? Qid, string Status, int? ImportanceScore,
    int? PopularityPercentile, bool HiddenGem, string? Classification, long AnnualPageviews, int PublishedVersion, string? DescriptionFr, IReadOnlyList<string> HeritageStatuses);

public sealed record InterestItem(string Code, double Weight);

public sealed record EthicsItem(bool Fragile, bool AccessRegulated);

public sealed record CrowdItem(int Offpeak, int Shoulder, int Peak);

public sealed record PlaceDetailItem(PlaceItem Place, IReadOnlyList<InterestItem> Interests, EthicsItem Ethics, CrowdItem Crowd, int? ImportanceOverride, bool Saturated);

public sealed record DedupItem(Guid Id, Guid KeptPlaceId, Guid OtherPlaceId, string Reason, double Similarity, double DistanceMeters, bool Automatic, DateTimeOffset CreatedAt, DateTimeOffset? RevertedAt);

public sealed record SourceDocumentItem(Guid Id, string Type, string Url, string Title, string? Publisher, string License, string Language, string? Revision, DateTimeOffset RetrievedAt, string Text, double Quality);

public sealed record FactItem(Guid Id, Guid DocumentId, string Statement, string Type, string Quote, double Confidence, string Status, string? Reason);

public sealed record PlaceFactsItem(IReadOnlyList<SourceDocumentItem> Documents, IReadOnlyList<FactItem> Facts);

public sealed record CheckIssueItem(string Check, string Detail, string? Sentence);

public sealed record VerifiedSentenceItem(string Sentence, string Verdict);

public sealed record OverlapItem(int LongestSharedWords, double FiveGramJaccard, bool Passes);

public sealed record CheckReportItem(IReadOnlyList<CheckIssueItem> Issues, IReadOnlyList<VerifiedSentenceItem> Sentences, OverlapItem? Overlap, int Attempts, IReadOnlyList<string> Uncertainties);

public sealed record StoryItem(
    Guid Id, Guid PlaceId, string Lang, string Kind, int Version, string Status, string Title, string Hook, string Text, string RemoteIntro,
    string AnnounceFront, string AnnounceLeft, string AnnounceRight, string? CareNote, IReadOnlyList<Guid> FactsUsed, int EstimatedDurationSeconds,
    string PromptVersion, string Model, double QualityScore, CheckReportItem Report, string VoiceId, double EditorialScore, string? RejectedReason,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt);

public sealed record AudioPartItem(string Part, string Path, string Sha256, int DurationSeconds, long Bytes);

public sealed record StoryReportItem(Guid Id, Guid StoryId, Guid TravelerId, string Reason, DateTimeOffset CreatedAt);

public sealed record StoryDetailItem(StoryItem Story, IReadOnlyList<AudioPartItem> Parts, IReadOnlyList<StoryReportItem> Reports, IReadOnlyList<FactItem> Facts);

public sealed record PronunciationItem(string Destination, string Term, string Replacement);

public sealed record AuditItem(Guid Id, DateTimeOffset At, string Actor, string Action, string Target, int Status, string? Detail);

/// <summary>An error the API explained with a Problem Details body; the page shows <see cref="Title"/> to the editor.</summary>
public sealed class AdminApiException(string title, int status) : Exception(title)
{
    public string Title { get; } = title;

    public int Status { get; } = status;
}

internal static class AdminJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}
