using System.Text.Json;
using OnVoyage.Factory.Application.Content;

namespace OnVoyage.Factory.Infrastructure.Sources;

/// <summary>Plain-text article extracts (<c>action=query&amp;prop=extracts&amp;explaintext=1</c>) with the revision id (§7.3 step 4). Redirects are followed.</summary>
internal sealed class WikipediaTextClient(WikimediaRequester requester) : IWikipediaTextClient
{
    public async Task<WikipediaText?> GetExtractAsync(string language, string title, CancellationToken cancellationToken)
    {
        if (language is not ("fr" or "en") || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var url = $"https://{language}.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&redirects=1&prop=extracts%7Crevisions&explaintext=1&exsectionformat=plain&rvprop=ids&titles={Uri.EscapeDataString(title)}";
        var body = await requester.GetStringAsync(() => new HttpRequestMessage(HttpMethod.Get, url), "application/json", cancellationToken);
        return body is null ? null : Parse(language, body);
    }

    internal static WikipediaText? Parse(string language, string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages) || pages.GetArrayLength() == 0)
        {
            return null;
        }

        var page = pages[0];
        if (page.TryGetProperty("missing", out _) || !page.TryGetProperty("extract", out var extract) || string.IsNullOrWhiteSpace(extract.GetString()))
        {
            return null;
        }

        var title = page.GetProperty("title").GetString()!;
        var revision = page.TryGetProperty("revisions", out var revisions) && revisions.GetArrayLength() > 0 && revisions[0].TryGetProperty("revid", out var revid)
            ? revid.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        return new WikipediaText(language, title, $"https://{language}.wikipedia.org/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}", extract.GetString()!, revision);
    }
}
