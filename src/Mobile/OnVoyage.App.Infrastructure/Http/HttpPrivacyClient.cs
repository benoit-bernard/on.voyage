using System.Net;
using System.Net.Http.Json;
using OnVoyage.App.Core.Privacy;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.App.Infrastructure.Http;

internal sealed class HttpPrivacyClient(HttpClient http) : IPrivacyApi
{
    public async Task<IReadOnlyList<ConsentDto>> GetConsentsAsync(CancellationToken cancellationToken) =>
        await Get<List<ConsentDto>>("api/platform/v1/me/consents", cancellationToken) ?? [];

    public async Task SetConsentAsync(string kind, bool granted, string textVersion, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync($"api/platform/v1/me/consents/{Uri.EscapeDataString(kind)}", new SetConsentRequest(granted, textVersion), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<SettingsDto> GetSettingsAsync(CancellationToken cancellationToken) =>
        await Get<SettingsDto>("api/discovery/v1/me/settings", cancellationToken) ?? new SettingsDto("fr", "balanced");

    public Task<SettingsDto> UpdateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken) => Send<SettingsPatch, SettingsDto>(HttpMethod.Patch, "api/discovery/v1/me/settings", patch, cancellationToken);

    public async Task<ProfileDto> GetProfileAsync(CancellationToken cancellationToken) =>
        await Get<ProfileDto>("api/discovery/v1/me/profile", cancellationToken) ?? throw new HttpRequestException("Empty profile.");

    public Task<ProfileDto> CorrectProfileAsync(IReadOnlyList<ProfileCorrection> corrections, CancellationToken cancellationToken) =>
        Send<ProfileCorrectionRequest, ProfileDto>(HttpMethod.Patch, "api/discovery/v1/me/profile", new ProfileCorrectionRequest(corrections), cancellationToken);

    public async Task<IReadOnlyList<HistoryItemDto>> GetHistoryAsync(CancellationToken cancellationToken) =>
        await Get<List<HistoryItemDto>>("api/discovery/v1/me/history", cancellationToken) ?? [];

    public async Task<InteractionBatchResponse> DeleteHistoryAsync(Guid poiId, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync($"api/discovery/v1/me/history/{poiId}", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InteractionBatchResponse>(cancellationToken) ?? throw new HttpRequestException("Empty answer.");
    }

    public async Task<ExportStatusDto> StartExportAsync(CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync("api/platform/v1/me/export", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ExportStatusDto>(cancellationToken) ?? throw new HttpRequestException("Empty answer.");
    }

    public async Task<ExportStatusDto> GetExportAsync(Guid exportId, CancellationToken cancellationToken) =>
        await Get<ExportStatusDto>($"api/platform/v1/me/export/{exportId}", cancellationToken) ?? throw new HttpRequestException("Unknown export.");

    public async Task<string> DownloadExportAsync(Guid exportId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"api/platform/v1/me/export/{exportId}/archive", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<DeletionStatusDto> RequestDeletionAsync(CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync("api/platform/v1/me/deletion", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DeletionStatusDto>(cancellationToken) ?? throw new HttpRequestException("Empty answer.");
    }

    public async Task<DeletionStatusDto?> GetDeletionAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("api/platform/v1/me/deletion", cancellationToken);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await Read<DeletionStatusDto>(response, cancellationToken);
    }

    private async Task<T?> Get<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        return await Read<T>(response, cancellationToken);
    }

    private async Task<TResponse> Send<TRequest, TResponse>(HttpMethod method, string url, TRequest body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        using var response = await http.SendAsync(request, cancellationToken);
        return await Read<TResponse>(response, cancellationToken) ?? throw new HttpRequestException("Empty answer.");
    }

    private static async Task<T?> Read<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }
}
