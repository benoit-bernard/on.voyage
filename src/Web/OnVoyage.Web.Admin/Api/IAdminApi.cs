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

    // Configuration and references (T-408)
    Task<IReadOnlyList<ConfigEntryDto>> ListConfigAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConfigEntryDto>> GetConfigHistoryAsync(string key, CancellationToken cancellationToken = default);

    Task SetConfigAsync(string key, JsonElement value, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeatureFlagDto>> ListFlagsAsync(CancellationToken cancellationToken = default);

    Task SetFlagAsync(string name, SetFeatureFlagRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PronunciationItem>> ListPronunciationsAsync(string destination, CancellationToken cancellationToken = default);

    Task SetPronunciationAsync(string destination, string term, string replacement, CancellationToken cancellationToken = default);

    Task DeletePronunciationAsync(string destination, string term, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditItem>> ListAuditAsync(int limit, CancellationToken cancellationToken = default);
}
