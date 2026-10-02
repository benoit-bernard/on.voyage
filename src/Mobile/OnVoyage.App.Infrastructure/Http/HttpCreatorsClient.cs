using System.Net;
using System.Net.Http.Json;
using OnVoyage.App.Core.Creators;
using OnVoyage.Creators.Contracts;
using OnVoyage.Discovery.Contracts;

namespace OnVoyage.App.Infrastructure.Http;

internal sealed class HttpCreatorsClient(HttpClient http) : ICreatorsClient
{
    public Task<PoiCreatorsDto?> GetPoiCreatorsAsync(Guid poiId, int limit, CancellationToken cancellationToken) =>
        GetAsync<PoiCreatorsDto>($"api/creators/v1/pois/{poiId}/contents?limit={limit}", cancellationToken);

    public Task<CreatorPageDto?> GetCreatorAsync(string handle, CancellationToken cancellationToken) =>
        GetAsync<CreatorPageDto>($"api/creators/v1/creators/{Uri.EscapeDataString(handle)}", cancellationToken);

    public async Task<FollowStateDto?> SetFollowAsync(Guid creatorId, bool following, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(following ? HttpMethod.Put : HttpMethod.Delete, $"api/creators/v1/me/follows/{creatorId}");
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FollowStateDto>(cancellationToken);
    }

    public async Task<ReportReceiptDto> ReportAsync(ReportRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("api/creators/v1/reports", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ReportReceiptDto>(cancellationToken)
            ?? throw new HttpRequestException("Creators returned an empty answer.");
    }

    public Task<CreatorsForMeDto?> GetCreatorsForMeAsync(string destination, CancellationToken cancellationToken) =>
        GetAsync<CreatorsForMeDto>($"api/discovery/v1/creators/for-me?destination={Uri.EscapeDataString(destination)}&limit=50", cancellationToken);

    private async Task<T?> GetAsync<T>(string url, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await http.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }
}
