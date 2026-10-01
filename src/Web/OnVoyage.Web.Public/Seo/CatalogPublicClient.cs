using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using OnVoyage.Catalog.Contracts;
using OnVoyage.ServiceDefaults.Security;

namespace OnVoyage.Web.Public.Seo;

/// <summary>What the public pages read from the Catalog. Only published, non-Premium text comes back; audio addresses are never used here.</summary>
public interface ICatalogPublicClient
{
    Task<DestinationDto?> GetDestinationAsync(string slug, CancellationToken cancellationToken);

    Task<IReadOnlyList<PoiSummaryDto>> GetPlacesAsync(string destination, CancellationToken cancellationToken);

    Task<PoiDetailDto?> GetPlaceAsync(string slug, CancellationToken cancellationToken);
}

/// <summary>Adds the short-lived <c>internal</c> token (SEC-03) to each call to the Catalog.</summary>
internal sealed class InternalTokenHandler(IConfiguration configuration, TimeProvider clock) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var secret = configuration["Auth:JwtSecret"] ?? throw new InvalidOperationException("Auth:JwtSecret is missing.");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", InternalTokens.Mint(secret, "web-public", clock));
        return base.SendAsync(request, cancellationToken);
    }
}

internal sealed class CatalogPublicClient(HttpClient http, IMemoryCache cache) : ICatalogPublicClient
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public Task<DestinationDto?> GetDestinationAsync(string slug, CancellationToken cancellationToken) =>
        Cached($"d:{slug}", async () =>
        {
            using var response = await http.GetAsync($"api/catalog/v1/destinations/{Uri.EscapeDataString(slug)}", cancellationToken);
            return response.StatusCode == HttpStatusCode.NotFound ? null : await Read<DestinationDto>(response, cancellationToken);
        });

    public async Task<IReadOnlyList<PoiSummaryDto>> GetPlacesAsync(string destination, CancellationToken cancellationToken) =>
        await Cached($"p:{destination}", async () =>
        {
            using var response = await http.GetAsync($"api/catalog/v1/destinations/{Uri.EscapeDataString(destination)}/pois?limit=500", cancellationToken);
            return response.StatusCode == HttpStatusCode.NotFound ? [] : await Read<List<PoiSummaryDto>>(response, cancellationToken) ?? [];
        }) ?? [];

    public Task<PoiDetailDto?> GetPlaceAsync(string slug, CancellationToken cancellationToken) =>
        Cached($"poi:{slug}", async () =>
        {
            using var response = await http.GetAsync($"api/catalog/v1/pois/{Uri.EscapeDataString(slug)}", cancellationToken);
            return response.StatusCode == HttpStatusCode.NotFound ? null : await Read<PoiDetailDto>(response, cancellationToken);
        });

    private async Task<T?> Cached<T>(string key, Func<Task<T?>> load)
    {
        if (cache.TryGetValue(key, out T? hit))
        {
            return hit;
        }

        var value = await load();
        cache.Set(key, value, Ttl);
        return value;
    }

    private static async Task<T?> Read<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }
}
