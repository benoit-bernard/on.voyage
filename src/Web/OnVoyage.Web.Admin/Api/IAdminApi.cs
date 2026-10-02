using System.Text.Json;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.Web.Admin.Api;

/// <summary>Everything the back-office does goes through the Gateway with the editor's own token; a page never talks to a database.</summary>
public interface IAdminApi
{
    Task<IReadOnlyList<DestinationItem>> GetDestinationsAsync(CancellationToken cancellationToken = default);

    // Places (T-402)
    Task<IReadOnlyList<PlaceItem>> ListPlacesAsync(string destination, string? status, int limit, CancellationToken cancellationToken = default);

    Task<PlaceDetailItem> GetPlaceAsync(Guid id, CancellationToken cancellationToken = default);

    Task PublishPlaceAsync(Guid id, CancellationToken cancellationToken = default);

    Task UnpublishPlaceAsync(Guid id, string reason, CancellationToken cancellationToken = default);

    Task RejectPlaceAsync(Guid id, CancellationToken cancellationToken = default);

    Task SetEditorialAsync(Guid id, int? importanceOverride, bool saturated, CancellationToken cancellationToken = default);

    Task SetEthicsAsync(Guid id, bool fragile, bool accessRegulated, CancellationToken cancellationToken = default);

    Task SetInterestsAsync(Guid id, IReadOnlyDictionary<string, double> weights, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DedupItem>> ListDedupAsync(string destination, CancellationToken cancellationToken = default);

    Task ConfirmMergeAsync(Guid linkId, CancellationToken cancellationToken = default);

    Task RevertMergeAsync(Guid linkId, CancellationToken cancellationToken = default);

    /// <param name="step"><c>imports</c>, <c>enrichments</c> or <c>scorings</c>.</param>
    Task StartPipelineStepAsync(string step, string destination, CancellationToken cancellationToken = default);

    // Content workshop (T-403)
    Task FetchSourcesAsync(Guid placeId, CancellationToken cancellationToken = default);

    Task ExtractFactsAsync(Guid placeId, CancellationToken cancellationToken = default);

    Task<PlaceFactsItem> GetFactsAsync(Guid placeId, CancellationToken cancellationToken = default);

    Task DecideFactAsync(Guid factId, bool accept, string? reason, CancellationToken cancellationToken = default);

    Task WriteStoryAsync(Guid placeId, string lang, string kind, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoryItem>> ListStoriesAsync(Guid placeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoryItem>> ListStoriesByStatusAsync(string status, int limit, CancellationToken cancellationToken = default);

    Task<StoryDetailItem> GetStoryAsync(Guid id, CancellationToken cancellationToken = default);

    Task EditStoryTextAsync(Guid id, string title, string text, CancellationToken cancellationToken = default);

    Task ApproveStoryAsync(Guid id, double? editorialScore, CancellationToken cancellationToken = default);

    Task RejectStoryAsync(Guid id, string reason, CancellationToken cancellationToken = default);

    Task RequestAudioAsync(Guid id, CancellationToken cancellationToken = default);

    Task ResetAudioAsync(Guid id, CancellationToken cancellationToken = default);

    Task ChangeVoiceAsync(Guid id, string voice, CancellationToken cancellationToken = default);

    Task PublishStoryAsync(Guid id, CancellationToken cancellationToken = default);

    Task SuspendStoryAsync(Guid id, string reason, CancellationToken cancellationToken = default);

    Task ResumeStoryAsync(Guid id, CancellationToken cancellationToken = default);

    Task<StoryItem> OpenCorrectionAsync(Guid id, CancellationToken cancellationToken = default);

    // Videos (T-407): the search runs on the server, with the server's key.
    Task<IReadOnlyList<VideoCandidateItem>> SearchVideosAsync(string query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlaceVideoItem>> ListVideosAsync(Guid placeId, CancellationToken cancellationToken = default);

    Task SelectVideoAsync(Guid placeId, string videoId, CancellationToken cancellationToken = default);

    Task RemoveVideoAsync(Guid placeId, string videoId, CancellationToken cancellationToken = default);

    // Mass generation (T-404)
    Task<Guid> CreateBatchAsync(NewBatch batch, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BatchProgressItem>> ListBatchesAsync(int limit, CancellationToken cancellationToken = default);

    Task<BatchDetailItem> GetBatchAsync(Guid id, CancellationToken cancellationToken = default);

    Task<int> RetryBatchAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Cancels the jobs that did not start; returns how many.</summary>
    Task<int> CancelBatchAsync(Guid id, CancellationToken cancellationToken = default);

    Task RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Messages the queue gave up on after its attempts.</summary>
    Task<IReadOnlyList<DeadLetterItem>> ListDeadLettersAsync(int limit, CancellationToken cancellationToken = default);

    // Bootstrap of a destination (ADR-0017): launched from here, run by the worker, followed with these.
    Task<Guid> StartBootstrapAsync(NewBootstrap bootstrap, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BootstrapRunItem>> ListBootstrapRunsAsync(int limit, CancellationToken cancellationToken = default);

    Task<BootstrapRunItem> GetBootstrapRunAsync(Guid id, CancellationToken cancellationToken = default);

    Task CancelBootstrapRunAsync(Guid id, CancellationToken cancellationToken = default);

    // KPIs (T-406)
    /// <summary>Null when the Insights service is not deployed or does not answer: the dashboard then says so instead of failing.</summary>
    Task<KpiReport?> GetKpisAsync(DateOnly from, DateOnly to, string? destination, string? cohort, CancellationToken cancellationToken = default);

    // Configuration and references (T-408)
    Task<IReadOnlyList<ConfigEntryDto>> ListConfigAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConfigEntryDto>> GetConfigHistoryAsync(string key, CancellationToken cancellationToken = default);

    Task SetConfigAsync(string key, JsonElement value, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeatureFlagDto>> ListFlagsAsync(CancellationToken cancellationToken = default);

    Task SetFlagAsync(string name, SetFeatureFlagRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PronunciationItem>> ListPronunciationsAsync(string destination, CancellationToken cancellationToken = default);

    Task SetPronunciationAsync(string destination, string term, string replacement, CancellationToken cancellationToken = default);

    Task DeletePronunciationAsync(string destination, string term, CancellationToken cancellationToken = default);

    /// <summary>The journal Platform keeps from every service's admin writes (SEC-10).</summary>
    Task<IReadOnlyList<AuditItem>> ListAuditAsync(int limit, string? service = null, CancellationToken cancellationToken = default);

    // Report inbox (T-405)
    Task<IReadOnlyList<ReportInboxItem>> ListReportInboxAsync(string? status, string? kind, int limit, CancellationToken cancellationToken = default);

    Task<int> ResolveReportsAsync(Guid storyId, string status, string? note, CancellationToken cancellationToken = default);
}
