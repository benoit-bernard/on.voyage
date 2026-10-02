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

public sealed record VideoCandidateItem(string VideoId, string Title, string Channel, string ThumbnailUrl, DateTimeOffset? PublishedAt);

public sealed record PlaceVideoItem(string VideoId, string Title, string Channel, string ThumbnailPath, string Url, DateTimeOffset SelectedAt);

public sealed record BatchCriteriaItem(string Destination, int? MinImportance, IReadOnlyList<string> PlaceStatuses, string Lang, string Kind, int Limit, double? BudgetUsd = null);

public sealed record BatchHeaderItem(Guid Id, DateTimeOffset CreatedAt, string CreatedBy, BatchCriteriaItem Criteria, int Total);

public sealed record BatchProgressItem(BatchHeaderItem Batch, int Pending, int Running, int Succeeded, int ToReview, int Failed, bool IsFinished, int Cancelled = 0, double CostUsd = 0d, string Status = "");

public sealed record JobItem(Guid Id, Guid PlaceId, string PlaceName, string State, string Step, int Attempts, string? LastError, Guid? StoryId, string? Outcome, DateTimeOffset UpdatedAt);

public sealed record BatchDetailItem(BatchProgressItem Progress, IReadOnlyList<JobItem> Jobs);

public sealed record NewBatch(string Destination, int? MinImportance, IReadOnlyList<string> PlaceStatuses, string Lang, string Kind, int Limit, double? BudgetUsd = null);

public sealed record DeadLetterItem(Guid Id, string MessageType, DateTimeOffset? At, string? ExceptionType, string? ExceptionMessage, Guid? JobId, Guid? BatchId, string? PlaceName);

public sealed record BootstrapStepItem(string Step, string Outcome, string? Detail);

public sealed record BootstrapRunItem(
    Guid Id, string Destination, string Status, string RequestedBy, DateTimeOffset RequestedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int MaxPlaces, int? MinImportance,
    string Lang, bool AutoPublish, double BudgetUsd, double CostUsd, int PlacesTotal, int PlacesDone, int Written, int ToReview, int Published, int Failed, string? Outcome, string? Error,
    bool CancelRequested, IReadOnlyList<BootstrapStepItem> Steps, bool IsFinished);

public sealed record NewBootstrap(string Destination, int MaxPlaces, int? MinImportance, string Lang, double BudgetUsd, bool AutoPublish, bool ForceImport, bool SkipImport);

public sealed record PronunciationItem(string Destination, string Term, string Replacement);

public sealed record AuditItem(Guid EventId, DateTimeOffset At, string Service, string Actor, string Action, string Target, int Status, string? Summary);

public sealed record ReportRemarkItem(string Reason, DateTimeOffset CreatedAt, string Status, string? Resolution);

public sealed record ReportInboxItem(
    Guid StoryId, Guid PlaceId, string PlaceName, string StoryTitle, string Lang, string Kind, int Version, string StoryStatus, int OpenReports,
    DateTimeOffset LatestAt, IReadOnlyList<ReportRemarkItem> Remarks);

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
