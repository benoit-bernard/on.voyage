using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using OnVoyage.App.Core.Interactions;
using OnVoyage.Discovery.Contracts;

namespace OnVoyage.App.Infrastructure.Http;

internal sealed class HttpDiscoveryClient(HttpClient http) : IDiscoveryClient
{
    public Task<InteractionBatchResponse> PostInteractionsAsync(IReadOnlyList<InteractionDto> batch, CancellationToken cancellationToken) =>
        PostAsync<InteractionBatchRequest, InteractionBatchResponse>("api/discovery/v1/me/interactions", new InteractionBatchRequest(batch), cancellationToken);

    public async Task<IReadOnlyList<OnboardingClipDto>> GetOnboardingClipsAsync(string lang, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"api/discovery/v1/onboarding/clips?lang={Uri.EscapeDataString(lang)}", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<OnboardingClipDto>>(cancellationToken) ?? [];
    }

    public Task<InteractionBatchResponse> PostOnboardingAsync(OnboardingRequest request, CancellationToken cancellationToken) =>
        PostAsync<OnboardingRequest, InteractionBatchResponse>("api/discovery/v1/onboarding", request, cancellationToken);

    public async Task<RecommendationItemDto?> GetSurpriseAsync(double? latitude, double? longitude, int radiusMeters, CancellationToken cancellationToken)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"api/discovery/v1/surprise?radius={radiusMeters}");
        if (latitude is { } lat && longitude is { } lng)
        {
            url += string.Create(CultureInfo.InvariantCulture, $"&lat={lat:0.###}&lng={lng:0.###}");
        }

        using var response = await http.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RecommendationItemDto>(cancellationToken);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string url, TRequest body, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(url, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken)
            ?? throw new HttpRequestException("Discovery returned an empty answer.");
    }
}
