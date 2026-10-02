using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using OnVoyage.Creators.Contracts;

namespace OnVoyage.Web.Public.Seo;

/// <summary>
/// What the public pages read from Creators (T-1204). Creators already returns only what is public: a published creator, validated links,
/// online contents, published places. The host presents an internal token, so <c>IsFollowing</c> is always false and no follower is involved.
/// </summary>
public interface ICreatorsPublicClient
{
    /// <summary>Null when the handle is unknown or the creator is not published.</summary>
    Task<CreatorPageDto?> GetCreatorAsync(string handle, CancellationToken cancellationToken);

    Task<PoiCreatorsDto?> GetPoiCreatorsAsync(Guid poiId, int limit, CancellationToken cancellationToken);

    /// <summary>The published creators who have a validated place in the destination (every page of the list).</summary>
    Task<IReadOnlyList<CreatorSummaryDto>> ListCreatorsAsync(string destination, CancellationToken cancellationToken);
}

internal sealed class CreatorsPublicClient(HttpClient http, IMemoryCache cache) : ICreatorsPublicClient
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private const int PageSize = 50;
    private const int MaxPages = 20;

    public Task<CreatorPageDto?> GetCreatorAsync(string handle, CancellationToken cancellationToken) =>
        Cached($"c:{handle.ToLowerInvariant()}", () => GetOrNull<CreatorPageDto>($"api/creators/v1/creators/{Uri.EscapeDataString(handle)}", cancellationToken));

    public Task<PoiCreatorsDto?> GetPoiCreatorsAsync(Guid poiId, int limit, CancellationToken cancellationToken) =>
        Cached($"pc:{poiId}:{limit}", () => GetOrNull<PoiCreatorsDto>($"api/creators/v1/pois/{poiId}/contents?limit={limit}", cancellationToken));

    public async Task<IReadOnlyList<CreatorSummaryDto>> ListCreatorsAsync(string destination, CancellationToken cancellationToken) =>
        await Cached($"cl:{destination}", async () =>
        {
            List<CreatorSummaryDto> all = [];
            string? cursor = null;
            for (var page = 0; page < MaxPages; page++)
            {
                var url = $"api/creators/v1/creators?destination={Uri.EscapeDataString(destination)}&limit={PageSize}" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
                var list = await GetOrNull<CreatorListDto>(url, cancellationToken);
                if (list is null)
                {
                    break;
                }

                all.AddRange(list.Items);
                cursor = list.NextCursor;
                if (cursor is null)
                {
                    break;
                }
            }

            return (IReadOnlyList<CreatorSummaryDto>?)all;
        }) ?? [];

    private async Task<T?> GetOrNull<T>(string url, CancellationToken cancellationToken)
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
}
