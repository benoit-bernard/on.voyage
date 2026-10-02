using OnVoyage.Creators.Contracts;
using OnVoyage.Web.Studio.Auth;

namespace OnVoyage.Web.Studio.Api;

internal sealed class HttpStudioApi(IHttpClientFactory clients, StudioSession session) : IStudioApi
{
    private const string Studio = "api/creators/v1/studio";

    private readonly GatewayCaller _gateway = new(clients, session);

    public Task<StudioRegistrationDto> GetRegistrationAsync(CancellationToken cancellationToken = default) =>
        _gateway.GetAsync<StudioRegistrationDto>($"{Studio}/registration", cancellationToken);

    public Task<StudioRegistrationDto> SignUpAsync(string handle, string displayName, string acceptedTermsVersion, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<StudioRegistrationDto>(HttpMethod.Post, $"{Studio}/signup", new StudioSignupRequest(handle, displayName, acceptedTermsVersion), cancellationToken);

    public Task<StudioRegistrationDto> AcceptTermsAsync(string acceptedTermsVersion, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<StudioRegistrationDto>(HttpMethod.Post, $"{Studio}/terms", new AcceptTermsRequest(acceptedTermsVersion), cancellationToken);

    public Task<StudioProfileDto> GetProfileAsync(CancellationToken cancellationToken = default) =>
        _gateway.GetAsync<StudioProfileDto>($"{Studio}/profile", cancellationToken);

    public Task<StudioProfileDto> UpdateProfileAsync(CreatorProfileRequest profile, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<StudioProfileDto>(HttpMethod.Put, $"{Studio}/profile", profile, cancellationToken);

    public Task<StudioProfileDto> PublishAsync(CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<StudioProfileDto>(HttpMethod.Post, $"{Studio}/publish", new { }, cancellationToken);

    public Task<StudioProfileDto> UnpublishAsync(CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<StudioProfileDto>(HttpMethod.Post, $"{Studio}/unpublish", new { }, cancellationToken);

    public Task<AdminContentDto> AddContentAsync(AddContentRequest content, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminContentDto>(HttpMethod.Post, $"{Studio}/contents", content, cancellationToken);

    public Task<AdminContentDto> UpdateContentAsync(Guid contentId, UpdateContentRequest content, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminContentDto>(HttpMethod.Put, $"{Studio}/contents/{contentId}", content, cancellationToken);

    public Task RemoveContentAsync(Guid contentId, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync(HttpMethod.Delete, $"{Studio}/contents/{contentId}", null, cancellationToken);

    public Task<IReadOnlyList<PoiSearchResultDto>> SearchPlacesAsync(string query, CancellationToken cancellationToken = default) =>
        _gateway.GetAsync<IReadOnlyList<PoiSearchResultDto>>($"{Studio}/places?query={Uri.EscapeDataString(query)}", cancellationToken);

    public Task<AdminPlaceLinkDto> AddPlaceLinkAsync(AddPlaceLinkRequest link, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminPlaceLinkDto>(HttpMethod.Post, $"{Studio}/place-links", link, cancellationToken);

    public Task<AdminPlaceLinkDto> SetPlaceLinkStatusAsync(Guid linkId, string status, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminPlaceLinkDto>(HttpMethod.Put, $"{Studio}/place-links/{linkId}", new SetPlaceLinkStatusRequest(status), cancellationToken);

    public Task RemovePlaceLinkAsync(Guid linkId, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync(HttpMethod.Delete, $"{Studio}/place-links/{linkId}", null, cancellationToken);

    public Task<PlaceProposalsDto> GetProposalsAsync(CancellationToken cancellationToken = default) =>
        _gateway.GetAsync<PlaceProposalsDto>($"{Studio}/place-links?status=proposed", cancellationToken);

    public Task<ReviewResultDto> ValidateProposalsAsync(IReadOnlyList<Guid>? linkIds, double? minConfidence, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<ReviewResultDto>(HttpMethod.Post, $"{Studio}/place-links/validate", new ReviewPlaceLinksRequest(linkIds, minConfidence), cancellationToken);

    public Task<ReviewResultDto> RejectProposalsAsync(IReadOnlyList<Guid> linkIds, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<ReviewResultDto>(HttpMethod.Post, $"{Studio}/place-links/reject", new ReviewPlaceLinksRequest(linkIds, null), cancellationToken);

    public Task<AdminPlaceLinkDto> CorrectProposalAsync(Guid linkId, Guid poiId, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminPlaceLinkDto>(HttpMethod.Post, $"{Studio}/place-links/{linkId}/correct", new CorrectPlaceLinkRequest(poiId), cancellationToken);

    public Task<AnalysisRequestedDto> AnalyzeAsync(bool force, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AnalysisRequestedDto>(HttpMethod.Post, $"{Studio}/place-links/analyze?force={(force ? "true" : "false")}", new { }, cancellationToken);

    public Task<ConnectionsDto> GetConnectionsAsync(CancellationToken cancellationToken = default) =>
        _gateway.GetAsync<ConnectionsDto>($"{Studio}/connections", cancellationToken);

    public Task<ConnectionStartDto> StartConnectionAsync(string platform, CancellationToken cancellationToken = default) =>
        _gateway.GetAsync<ConnectionStartDto>($"{Studio}/connections/{Uri.EscapeDataString(platform)}/start", cancellationToken);

    public Task<ConnectedAccountDto> CompleteConnectionAsync(string platform, string code, string state, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<ConnectedAccountDto>(HttpMethod.Post, $"{Studio}/connections/{Uri.EscapeDataString(platform)}/callback", new CompleteConnectionRequest(code, state), cancellationToken);

    public Task DisconnectAsync(string platform, bool deleteContents, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync(HttpMethod.Delete, $"{Studio}/connections/{Uri.EscapeDataString(platform)}?deleteContents={(deleteContents ? "true" : "false")}", null, cancellationToken);

    public Task<SyncRequestedDto> RequestSyncAsync(CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<SyncRequestedDto>(HttpMethod.Post, $"{Studio}/sync", new { }, cancellationToken);

    public Task<AdminTipDto> SetTipAsync(Guid poiId, string text, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync<AdminTipDto>(HttpMethod.Put, $"{Studio}/tips/{poiId}", new SetTipRequest(text), cancellationToken);

    public Task RemoveTipAsync(Guid poiId, CancellationToken cancellationToken = default) =>
        _gateway.WriteAsync(HttpMethod.Delete, $"{Studio}/tips/{poiId}", null, cancellationToken);
}
