using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using OnVoyage.App.Core.Catalog;
using OnVoyage.Catalog.Contracts;

namespace OnVoyage.App.Infrastructure.Http;

internal sealed class HttpCatalogClient(HttpClient http) : ICatalogClient
{
    public async Task<DestinationDto?> GetDestinationAsync(string slug, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"api/catalog/v1/destinations/{Uri.EscapeDataString(slug)}", cancellationToken);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await ReadAsync<DestinationDto>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<PoiSummaryDto>> GetPoisAsync(string destination, double? latitude, double? longitude, CancellationToken cancellationToken)
    {
        var url = $"api/catalog/v1/destinations/{Uri.EscapeDataString(destination)}/pois";
        if (latitude is { } lat && longitude is { } lon)
        {
            url += string.Create(CultureInfo.InvariantCulture, $"?lat={lat:0.###}&lon={lon:0.###}");
        }

        using var response = await http.GetAsync(url, cancellationToken);
        return await ReadAsync<List<PoiSummaryDto>>(response, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<PoiSummaryDto>> SearchAsync(string destination, string text, int limit, CancellationToken cancellationToken)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"api/catalog/v1/search?q={Uri.EscapeDataString(text)}&destination={Uri.EscapeDataString(destination)}&limit={limit}");
        using var response = await http.GetAsync(url, cancellationToken);
        return await ReadAsync<List<PoiSummaryDto>>(response, cancellationToken) ?? [];
    }

    public async Task<PoiDetailDto?> GetPoiAsync(string slug, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"api/catalog/v1/pois/{Uri.EscapeDataString(slug)}", cancellationToken);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await ReadAsync<PoiDetailDto>(response, cancellationToken);
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }
}
