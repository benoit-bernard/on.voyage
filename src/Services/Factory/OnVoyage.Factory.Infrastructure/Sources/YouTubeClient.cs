using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Features.Videos;

namespace OnVoyage.Factory.Infrastructure.Sources;

/// <summary>
/// YouTube Data API v3 from the server. The key travels in the <c>x-goog-api-key</c> header, not in the URL, so the HTTP client logs
/// (which print the address) never contain it. A search costs 100 quota units: the back office only searches when an editor asks.
/// </summary>
internal sealed class YouTubeClient(HttpClient http, IConfiguration configuration) : IVideoSearch
{
    public const string BaseAddress = "https://www.googleapis.com/youtube/v3/";
    private const int MaxThumbnailBytes = 1_000_000;

    private string? Key => configuration["YouTube:ApiKey"];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Key);

    public async Task<IReadOnlyList<VideoCandidate>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var url = $"search?part=snippet&type=video&maxResults={Math.Clamp(maxResults, 1, 25)}&relevanceLanguage=fr&safeSearch=strict&q={Uri.EscapeDataString(query)}";
        using var document = await GetJsonAsync(url, cancellationToken);
        return Parse(document.RootElement, searchResult: true);
    }

    public async Task<VideoCandidate?> GetAsync(string videoId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"videos?part=snippet&id={Uri.EscapeDataString(videoId)}", cancellationToken);
        var found = Parse(document.RootElement, searchResult: false);
        return found.Count > 0 ? found[0] : null;
    }

    public async Task<byte[]?> DownloadThumbnailAsync(string url, CancellationToken cancellationToken)
    {
        if (!IsYouTubeImage(url))
        {
            return null;
        }

        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "image/jpeg" || response.Content.Headers.ContentLength > MaxThumbnailBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxThumbnailBytes)
                {
                    return null;
                }
            }

            return buffer.ToArray();
        }
        catch (HttpRequestException exception)
        {
            throw new ExternalServiceException("The thumbnail could not be downloaded.", exception);
        }
    }

    /// <summary>Only YouTube's image hosts over https: the address comes from an API answer, never fetch an arbitrary one.</summary>
    internal static bool IsYouTubeImage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.EndsWith(".ytimg.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("img.youtube.com", StringComparison.OrdinalIgnoreCase));

    private async Task<JsonDocument> GetJsonAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(BaseAddress), relativeUrl));
        request.Headers.Add("x-goog-api-key", Key);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError)
            {
                throw new ExternalServiceException($"YouTube answered {(int)response.StatusCode}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ExternalServiceException($"YouTube refused the request ({(int)response.StatusCode}).");
            }

            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new ExternalServiceException("YouTube could not be reached.", exception);
        }
        catch (JsonException exception)
        {
            throw new ExternalServiceException("YouTube sent an unreadable answer.", exception);
        }
    }

    /// <summary>Reads both answer shapes: <c>search</c> (<c>id.videoId</c>) and <c>videos</c> (<c>id</c> is the id).</summary>
    internal static IReadOnlyList<VideoCandidate> Parse(JsonElement root, bool searchResult)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<VideoCandidate> candidates = [];
        foreach (var item in items.EnumerateArray())
        {
            var id = searchResult
                ? item.TryGetProperty("id", out var idObject) && idObject.ValueKind == JsonValueKind.Object && idObject.TryGetProperty("videoId", out var videoId) ? videoId.GetString() : null
                : item.TryGetProperty("id", out var plain) && plain.ValueKind == JsonValueKind.String ? plain.GetString() : null;
            if (id is null || !VideoRules.IsVideoId(id) || !item.TryGetProperty("snippet", out var snippet))
            {
                continue;
            }

            var thumbnail = Thumbnail(snippet);
            var title = snippet.TryGetProperty("title", out var titleValue) ? WebUtility.HtmlDecode(titleValue.GetString() ?? string.Empty) : string.Empty;
            if (thumbnail is null || title.Length == 0)
            {
                continue;
            }

            var channel = snippet.TryGetProperty("channelTitle", out var channelValue) ? WebUtility.HtmlDecode(channelValue.GetString() ?? string.Empty) : string.Empty;
            DateTimeOffset? published = snippet.TryGetProperty("publishedAt", out var date) && date.TryGetDateTimeOffset(out var parsed) ? parsed : null;
            candidates.Add(new VideoCandidate(id, title, channel, thumbnail, published));
        }

        return candidates;
    }

    private static string? Thumbnail(JsonElement snippet)
    {
        if (!snippet.TryGetProperty("thumbnails", out var thumbnails) || thumbnails.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var size in new[] { "medium", "high", "default" })
        {
            if (thumbnails.TryGetProperty(size, out var entry) && entry.TryGetProperty("url", out var url) && url.GetString() is { } value)
            {
                return value;
            }
        }

        return null;
    }
}
