using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OnVoyage.Platform.Contracts;
using OnVoyage.Web.Admin.Auth;

namespace OnVoyage.Web.Admin.Api;

internal sealed class HttpAdminApi(IHttpClientFactory clients, AdminSession session) : IAdminApi
{
    public const string ClientName = GatewayCaller.ClientName;
    private const string Factory = "api/factory/v1/admin";
    private const string Platform = "api/platform/v1/admin";

    private readonly GatewayCaller _gateway = new(clients, session);

    private Task<T> GetAsync<T>(string path, CancellationToken cancellationToken) => _gateway.GetAsync<T>(path, cancellationToken);

    private Task<T> WriteAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken) => _gateway.WriteAsync<T>(method, path, body, cancellationToken);

    private Task WriteAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken) => _gateway.WriteAsync(method, path, body, cancellationToken);

    public Task<IReadOnlyList<DestinationItem>> GetDestinationsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<DestinationItem>>($"{Factory}/destinations", cancellationToken);

    public Task<IReadOnlyList<PlaceItem>> ListPlacesAsync(string destination, string? status, int limit, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<PlaceItem>>($"{Factory}/places?destination={Uri.EscapeDataString(destination)}&limit={limit}{(string.IsNullOrEmpty(status) ? string.Empty : $"&status={Uri.EscapeDataString(status)}")}", cancellationToken);

    public Task<PlaceDetailItem> GetPlaceAsync(Guid id, CancellationToken cancellationToken = default) => GetAsync<PlaceDetailItem>($"{Factory}/places/{id}", cancellationToken);

    public Task PublishPlaceAsync(Guid id, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Post, $"{Factory}/places/{id}/publish", null, cancellationToken);

    public Task UnpublishPlaceAsync(Guid id, string reason, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Post, $"{Factory}/places/{id}/unpublish", new { reason }, cancellationToken);

    public Task RejectPlaceAsync(Guid id, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Post, $"{Factory}/places/{id}/reject", null, cancellationToken);

    public Task SetEditorialAsync(Guid id, int? importanceOverride, bool saturated, CancellationToken cancellationToken = default) =>
        WriteAsync(HttpMethod.Put, $"{Factory}/places/{id}/editorial", new { importanceOverride, editoriallySaturated = saturated }, cancellationToken);

    public Task SetEthicsAsync(Guid id, bool fragile, bool accessRegulated, CancellationToken cancellationToken = default) =>
        WriteAsync(HttpMethod.Put, $"{Factory}/places/{id}/ethics", new { fragile, accessRegulated }, cancellationToken);

    public Task SetInterestsAsync(Guid id, IReadOnlyDictionary<string, double> weights, CancellationToken cancellationToken = default) =>
        WriteAsync(HttpMethod.Put, $"{Factory}/places/{id}/interests", new { weights }, cancellationToken);

    public Task<IReadOnlyList<DedupItem>> ListDedupAsync(string destination, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<DedupItem>>($"{Factory}/dedup?destination={Uri.EscapeDataString(destination)}", cancellationToken);

    public Task ConfirmMergeAsync(Guid linkId, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Post, $"{Factory}/dedup/{linkId}/confirm", null, cancellationToken);

    public Task RevertMergeAsync(Guid linkId, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Post, $"{Factory}/dedup/{linkId}/revert", null, cancellationToken);

    public Task StartPipelineStepAsync(string step, string destination, CancellationToken cancellationToken = default) =>
        step is "imports" or "enrichments" or "scorings"
            ? WriteAsync(HttpMethod.Post, $"{Factory}/{step}", new { destination }, cancellationToken)
            : throw new ArgumentOutOfRangeException(nameof(step));

    public Task FetchSourcesAsync(Guid placeId, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Post, $"{Factory}/places/{placeId}/sources", null, cancellationToken);

    public Task ExtractFactsAsync(Guid placeId, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Post, $"{Factory}/places/{placeId}/facts/extraction", null, cancellationToken);

    public Task<PlaceFactsItem> GetFactsAsync(Guid placeId, CancellationToken cancellationToken = default) => GetAsync<PlaceFactsItem>($"{Factory}/places/{placeId}/facts", cancellationToken);

    public Task DecideFactAsync(Guid factId, bool accept, string? reason, CancellationToken cancellationToken = default) =>
        WriteAsync(HttpMethod.Post, $"{Factory}/facts/{factId}/decision", new { accept, reason }, cancellationToken);

    public Task WriteStoryAsync(Guid placeId, string lang, string kind, CancellationToken cancellationToken = default) =>
        WriteAsync(HttpMethod.Post, $"{Factory}/places/{placeId}/stories", new { lang, kind }, cancellationToken);

    public Task<IReadOnlyList<StoryItem>> ListStoriesAsync(Guid placeId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<StoryItem>>($"{Factory}/places/{placeId}/stories", cancellationToken);

    public Task<IReadOnlyList<StoryItem>> ListStoriesByStatusAsync(string status, int limit, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<StoryItem>>($"{Factory}/stories?status={Uri.EscapeDataString(status)}&limit={limit}", cancellationToken);

    public Task<StoryDetailItem> GetStoryAsync(Guid id, CancellationToken cancellationToken = default) => GetAsync<StoryDetailItem>($"{Factory}/stories/{id}", cancellationToken);

    public Task EditStoryTextAsync(Guid id, string title, string text, CancellationToken cancellationToken = default) =>
        WriteAsync(HttpMethod.Put, $"{Factory}/stories/{id}/text", new { title, text }, cancellationToken);

    public Task ApproveStoryAsync(Guid id, double? editorialScore, CancellationToken cancellationToken = default) =>
        WriteAsync(HttpMethod.Post, $"{Factory}/stories/{id}/approve", new { editorialScore }, cancellationToken);

    public Task RejectStoryAsync(Guid id, string reason, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Post, $"{Factory}/stories/{id}/reject", new { reason }, cancellationToken);

    public Task RequestAudioAsync(Guid id, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Post, $"{Factory}/stories/{id}/audio", null, cancellationToken);

    public Task ResetAudioAsync(Guid id, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Post, $"{Factory}/stories/{id}/audio/reset", null, cancellationToken);

    public Task ChangeVoiceAsync(Guid id, string voice, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Put, $"{Factory}/stories/{id}/voice", new { voice }, cancellationToken);

    public Task PublishStoryAsync(Guid id, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Post, $"{Factory}/stories/{id}/publish", null, cancellationToken);

    public Task SuspendStoryAsync(Guid id, string reason, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Post, $"{Factory}/stories/{id}/suspend", new { reason }, cancellationToken);

    public Task ResumeStoryAsync(Guid id, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Post, $"{Factory}/stories/{id}/resume", null, cancellationToken);

    public Task<StoryItem> OpenCorrectionAsync(Guid id, CancellationToken cancellationToken = default) => WriteAsync<StoryItem>(HttpMethod.Post, $"{Factory}/stories/{id}/correction", new { }, cancellationToken);

    public Task<IReadOnlyList<VideoCandidateItem>> SearchVideosAsync(string query, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<VideoCandidateItem>>($"{Factory}/videos/search?q={Uri.EscapeDataString(query)}", cancellationToken);

    public Task<IReadOnlyList<PlaceVideoItem>> ListVideosAsync(Guid placeId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<PlaceVideoItem>>($"{Factory}/places/{placeId}/videos", cancellationToken);

    public Task<VideoQuotaItem> GetVideoQuotaAsync(CancellationToken cancellationToken = default) => GetAsync<VideoQuotaItem>($"{Factory}/videos/quota", cancellationToken);

    public Task<IReadOnlyList<SelectedVideoItem>> ListSelectedVideosAsync(int limit, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<SelectedVideoItem>>($"{Factory}/videos?limit={limit}", cancellationToken);

    public Task SelectVideoAsync(Guid placeId, string videoId, CancellationToken cancellationToken = default) =>
        WriteAsync(HttpMethod.Post, $"{Factory}/places/{placeId}/videos", new { videoId }, cancellationToken);

    public Task RemoveVideoAsync(Guid placeId, string videoId, CancellationToken cancellationToken = default) =>
        WriteAsync(HttpMethod.Delete, $"{Factory}/places/{placeId}/videos/{Uri.EscapeDataString(videoId)}", null, cancellationToken);

    public async Task<Guid> CreateBatchAsync(NewBatch batch, CancellationToken cancellationToken = default) =>
        (await WriteAsync<JsonElement>(HttpMethod.Post, $"{Factory}/batches", new { batch.Destination, batch.MinImportance, batch.PlaceStatuses, batch.Lang, batch.Kind, batch.Limit, batch.BudgetUsd }, cancellationToken)).GetProperty("id").GetGuid();

    public Task<IReadOnlyList<BatchProgressItem>> ListBatchesAsync(int limit, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<BatchProgressItem>>($"{Factory}/batches?limit={limit}", cancellationToken);

    public Task<BatchDetailItem> GetBatchAsync(Guid id, CancellationToken cancellationToken = default) => GetAsync<BatchDetailItem>($"{Factory}/batches/{id}", cancellationToken);

    public async Task<int> RetryBatchAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await WriteAsync<JsonElement>(HttpMethod.Post, $"{Factory}/batches/{id}/retry", null, cancellationToken)).GetProperty("requeued").GetInt32();

    public async Task<int> CancelBatchAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await WriteAsync<JsonElement>(HttpMethod.Post, $"{Factory}/batches/{id}/cancel", null, cancellationToken)).GetProperty("cancelled").GetInt32();

    public Task RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Post, $"{Factory}/batch-jobs/{jobId}/retry", null, cancellationToken);

    public Task<IReadOnlyList<DeadLetterItem>> ListDeadLettersAsync(int limit, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<DeadLetterItem>>($"{Factory}/dead-letters?limit={limit}", cancellationToken);

    public async Task<Guid> StartBootstrapAsync(NewBootstrap bootstrap, CancellationToken cancellationToken = default) =>
        (await WriteAsync<JsonElement>(HttpMethod.Post, $"{Factory}/bootstrap", new { bootstrap.Destination, bootstrap.MaxPlaces, bootstrap.MinImportance, bootstrap.Lang, bootstrap.BudgetUsd, bootstrap.AutoPublish, bootstrap.ForceImport, bootstrap.SkipImport }, cancellationToken)).GetProperty("id").GetGuid();

    public Task<IReadOnlyList<BootstrapRunItem>> ListBootstrapRunsAsync(int limit, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<BootstrapRunItem>>($"{Factory}/bootstrap-runs?limit={limit}", cancellationToken);

    public Task<BootstrapRunItem> GetBootstrapRunAsync(Guid id, CancellationToken cancellationToken = default) => GetAsync<BootstrapRunItem>($"{Factory}/bootstrap-runs/{id}", cancellationToken);

    public Task CancelBootstrapRunAsync(Guid id, CancellationToken cancellationToken = default) => WriteAsync(HttpMethod.Post, $"{Factory}/bootstrap-runs/{id}/cancel", null, cancellationToken);

    public async Task<KpiReport?> GetKpisAsync(DateOnly from, DateOnly to, string? destination, string? cohort, CancellationToken cancellationToken = default)
    {
        var query = $"from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}{(string.IsNullOrEmpty(destination) ? string.Empty : $"&destination={Uri.EscapeDataString(destination)}")}{(string.IsNullOrEmpty(cohort) ? string.Empty : $"&cohort={Uri.EscapeDataString(cohort)}")}";
        try
        {
            return await GetAsync<KpiReport>($"api/insights/v1/kpis?{query}", cancellationToken);
        }
        catch (AdminApiException exception) when (exception.Status is 404 or 502 or 503 or 504)
        {
            return null; // no Insights behind the gateway yet
        }
    }

    public Task<IReadOnlyList<ConfigEntryDto>> ListConfigAsync(CancellationToken cancellationToken = default) => GetAsync<IReadOnlyList<ConfigEntryDto>>($"{Platform}/config", cancellationToken);

    public Task<IReadOnlyList<ConfigEntryDto>> GetConfigHistoryAsync(string key, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ConfigEntryDto>>($"{Platform}/config/{Uri.EscapeDataString(key)}/history", cancellationToken);

    public Task SetConfigAsync(string key, JsonElement value, CancellationToken cancellationToken = default) =>
        WriteAsync(HttpMethod.Put, $"{Platform}/config/{Uri.EscapeDataString(key)}", new SetConfigRequest(value), cancellationToken);

    public Task<IReadOnlyList<FeatureFlagDto>> ListFlagsAsync(CancellationToken cancellationToken = default) => GetAsync<IReadOnlyList<FeatureFlagDto>>($"{Platform}/flags", cancellationToken);

    public Task SetFlagAsync(string name, SetFeatureFlagRequest request, CancellationToken cancellationToken = default) =>
        WriteAsync(HttpMethod.Put, $"{Platform}/flags/{Uri.EscapeDataString(name)}", request, cancellationToken);

    public Task<IReadOnlyList<PronunciationItem>> ListPronunciationsAsync(string destination, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<PronunciationItem>>($"{Factory}/pronunciations?destination={Uri.EscapeDataString(destination)}", cancellationToken);

    public Task SetPronunciationAsync(string destination, string term, string replacement, CancellationToken cancellationToken = default) =>
        WriteAsync(HttpMethod.Put, $"{Factory}/pronunciations/{Uri.EscapeDataString(destination)}/{Uri.EscapeDataString(term)}", new { replacement }, cancellationToken);

    public Task DeletePronunciationAsync(string destination, string term, CancellationToken cancellationToken = default) =>
        WriteAsync(HttpMethod.Delete, $"{Factory}/pronunciations/{Uri.EscapeDataString(destination)}/{Uri.EscapeDataString(term)}", null, cancellationToken);

    public Task<IReadOnlyList<AuditItem>> ListAuditAsync(int limit, string? service = null, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<AuditItem>>($"{Platform}/audit?limit={limit}{(string.IsNullOrEmpty(service) ? string.Empty : $"&service={Uri.EscapeDataString(service)}")}", cancellationToken);

    public Task<IReadOnlyList<ReportInboxItem>> ListReportInboxAsync(string? status, string? kind, int limit, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ReportInboxItem>>($"{Factory}/reports?limit={limit}{(string.IsNullOrEmpty(status) ? string.Empty : $"&status={Uri.EscapeDataString(status)}")}{(string.IsNullOrEmpty(kind) ? string.Empty : $"&kind={Uri.EscapeDataString(kind)}")}", cancellationToken);

    public async Task<int> ResolveReportsAsync(Guid storyId, string status, string? note, CancellationToken cancellationToken = default) =>
        (await WriteAsync<JsonElement>(HttpMethod.Post, $"{Factory}/stories/{storyId}/reports/resolve", new { status, note }, cancellationToken)).GetProperty("closed").GetInt32();
}
