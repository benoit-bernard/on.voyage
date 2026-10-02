using OnVoyage.Creators.Contracts;
using OnVoyage.Web.Admin.Auth;

namespace OnVoyage.Web.Admin.Api;

internal sealed class HttpCreatorsAdminApi(IHttpClientFactory clients, AdminSession session) : ICreatorsAdminApi
{
    private const string Admin = "api/creators/v1/admin";

    private readonly GatewayCaller _gateway = new(clients, session);

    public Task<IReadOnlyList<AdminCreatorSummaryDto>> ListCreatorsAsync(string? status, string? search, CancellationToken cancellationToken = default) =>
        _gateway.GetAsync<IReadOnlyList<AdminCreatorSummaryDto>>($"{Admin}/creators?limit=200{Param("status", status)}{Param("search", search)}", cancellationToken);

    public Task<AdminCreatorDetailDto> GetCreatorAsync(Guid id, CancellationToken cancellationToken = default) =>
        _gateway.GetAsync<AdminCreatorDetailDto>($"{Admin}/creators/{id}", cancellationToken);

    public Task<AdminCreatorDetailDto> CreateFounderAsync(CreatorProfileRequest profile, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminCreatorDetailDto>(HttpMethod.Post, $"{Admin}/creators", profile, cancellationToken);

    public Task<AdminCreatorDetailDto> UpdateCreatorAsync(Guid id, CreatorProfileRequest profile, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminCreatorDetailDto>(HttpMethod.Put, $"{Admin}/creators/{id}", profile, cancellationToken);

    public Task<AdminCreatorDetailDto> RecordConsentAsync(Guid id, string documentRef, DateTimeOffset? acceptedAt, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminCreatorDetailDto>(HttpMethod.Put, $"{Admin}/creators/{id}/consent", new FounderConsentRequest(documentRef, acceptedAt), cancellationToken);

    public Task<AdminCreatorDetailDto> LinkAccountAsync(Guid id, Guid accountId, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminCreatorDetailDto>(HttpMethod.Put, $"{Admin}/creators/{id}/account", new LinkAccountRequest(accountId), cancellationToken);

    public Task<AdminCreatorDetailDto> PublishAsync(Guid id, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminCreatorDetailDto>(HttpMethod.Post, $"{Admin}/creators/{id}/publish", new { }, cancellationToken);

    public Task<AdminCreatorDetailDto> UnpublishAsync(Guid id, string reason, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminCreatorDetailDto>(HttpMethod.Post, $"{Admin}/creators/{id}/unpublish", new ReasonRequest(reason), cancellationToken);

    public Task<AdminCreatorDetailDto> SuspendAsync(Guid id, string reason, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminCreatorDetailDto>(HttpMethod.Post, $"{Admin}/creators/{id}/suspend", new ReasonRequest(reason), cancellationToken);

    public Task<AdminCreatorDetailDto> ClaimHandleAsync(Guid id, string handle, string reason, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminCreatorDetailDto>(HttpMethod.Post, $"{Admin}/creators/{id}/handle-claim", new ClaimHandleRequest(handle, reason), cancellationToken);

    public Task<AdminContentDto> AddContentAsync(Guid id, AddContentRequest content, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminContentDto>(HttpMethod.Post, $"{Admin}/creators/{id}/contents", content, cancellationToken);

    public Task RemoveContentAsync(Guid id, Guid contentId, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync(HttpMethod.Delete, $"{Admin}/creators/{id}/contents/{contentId}", null, cancellationToken);

    public Task<IReadOnlyList<PoiSearchResultDto>> SearchPlacesAsync(string query, CancellationToken cancellationToken = default) =>
        _gateway.GetAsync<IReadOnlyList<PoiSearchResultDto>>($"{Admin}/places?limit=20&query={Uri.EscapeDataString(query)}", cancellationToken);

    public Task<AdminPlaceLinkDto> AddPlaceLinkAsync(Guid id, AddPlaceLinkRequest link, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminPlaceLinkDto>(HttpMethod.Post, $"{Admin}/creators/{id}/place-links", link, cancellationToken);

    public Task<AdminPlaceLinkDto> SetPlaceLinkStatusAsync(Guid id, Guid linkId, string status, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminPlaceLinkDto>(HttpMethod.Put, $"{Admin}/creators/{id}/place-links/{linkId}", new SetPlaceLinkStatusRequest(status), cancellationToken);

    public Task RemovePlaceLinkAsync(Guid id, Guid linkId, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync(HttpMethod.Delete, $"{Admin}/creators/{id}/place-links/{linkId}", null, cancellationToken);

    public Task<AdminTipDto> SetTipAsync(Guid id, Guid poiId, string text, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminTipDto>(HttpMethod.Put, $"{Admin}/creators/{id}/tips/{poiId}", new SetTipRequest(text), cancellationToken);

    public Task RemoveTipAsync(Guid id, Guid poiId, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync(HttpMethod.Delete, $"{Admin}/creators/{id}/tips/{poiId}", null, cancellationToken);

    public Task<IReadOnlyList<ModerationCaseDto>> ListModerationAsync(string? status, CancellationToken cancellationToken = default) =>
        _gateway.GetAsync<IReadOnlyList<ModerationCaseDto>>($"{Admin}/moderation?limit=200{Param("status", status)}", cancellationToken);

    public Task<ModerationCaseDto> DecideAsync(Guid caseId, string decision, string? statementOfReasons, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<ModerationCaseDto>(HttpMethod.Post, $"{Admin}/moderation/{caseId}/decision", new DecideCaseRequest(decision, statementOfReasons), cancellationToken);

    private static string Param(string name, string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : $"&{name}={Uri.EscapeDataString(value)}";
}
