using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using OnVoyage.Creators.Contracts;

namespace OnVoyage.Web.Public.Seo;

internal static class SeoEndpoints
{
    private static readonly string[] AppPaths = ["/app/*", "/fr/*", "/en/*"];
    private static readonly string[] HandleAllUrls = ["delegate_permission/common.handle_all_urls"];
    private static readonly string[] LegalPages = ["conditions", "confidentialite", "mentions", "sources"];
    private static readonly string[] AllowedEngines = ["Googlebot", "Bingbot", "Qwantbot", "Applebot", "DuckDuckBot"];

    public static IEndpointRouteBuilder MapSeoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/robots.txt", (BlockedCrawlers blocked, PublicSettings settings) => Results.Text(Robots(blocked, settings), "text/plain; charset=utf-8"));

        // Annexe B: the reservation is declared for the whole site.
        app.MapGet("/.well-known/tdmrep.json", (PublicSettings settings) => Results.Text(
            JsonSerializer.Serialize(new[] { new Dictionary<string, object> { ["location"] = "/", ["tdm-reservation"] = 1, ["tdm-policy"] = settings.Absolute(PublicSettings.TdmPolicyPath) } }),
            "application/json"));

        app.MapGet("/sitemap.xml", async (ICatalogPublicClient catalog, ICreatorsPublicClient creators, ContentStore content, PublicSettings settings, CancellationToken ct) =>
            Results.Text(await Sitemap(catalog, creators, content, settings, ct), "application/xml; charset=utf-8"));

        // Universal links / App Links: the identifiers are placeholders until the developer accounts exist (H-003).
        app.MapGet("/.well-known/apple-app-site-association", (PublicSettings settings) => Results.Text(
            JsonSerializer.Serialize(new { applinks = new { apps = Array.Empty<string>(), details = new[] { new { appID = settings.AppleAppId, paths = AppPaths } } } }),
            "application/json"));
        app.MapGet("/.well-known/assetlinks.json", (PublicSettings settings) => Results.Text(
            JsonSerializer.Serialize(new[]
            {
                new
                {
                    relation = HandleAllUrls,
                    target = new { @namespace = "android_app", package_name = settings.AndroidPackage, sha256_cert_fingerprints = new[] { settings.AndroidCertSha256 } },
                },
            }),
            "application/json"));
        return app;
    }

    public static string Robots(BlockedCrawlers blocked, PublicSettings settings)
    {
        var text = new StringBuilder();
        text.AppendLine("# ON.VOYAGE — robots.txt").AppendLine("# Moteurs de recherche autorisés");
        foreach (var engine in AllowedEngines)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"User-agent: {engine}");
        }

        text.AppendLine("Allow: /").AppendLine("Disallow: /admin/").AppendLine("Disallow: /api/").AppendLine();
        text.AppendLine("# Robots d'entraînement et d'assistants IA : interdits");
        foreach (var token in blocked.Tokens)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"User-agent: {token}");
        }

        text.AppendLine("Disallow: /").AppendLine();
        text.AppendLine("# Tous les autres").AppendLine("User-agent: *").AppendLine("Allow: /").AppendLine("Disallow: /admin/").AppendLine("Disallow: /api/").AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Sitemap: {settings.Absolute("/sitemap.xml")}");
        return text.ToString();
    }

    private static async Task<string> Sitemap(ICatalogPublicClient catalog, ICreatorsPublicClient creators, ContentStore content, PublicSettings settings, CancellationToken cancellationToken)
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        var urls = new List<XElement> { Url(ns, settings.Absolute("/")) };

        foreach (var slug in settings.Destinations)
        {
            if (await catalog.GetDestinationAsync(slug, cancellationToken) is null)
            {
                continue;
            }

            foreach (var lang in settings.Languages)
            {
                urls.Add(Url(ns, settings.Absolute($"/{lang}/{slug}"), Alternates(xhtml, settings, settings.Languages.Select(l => (l, $"/{l}/{slug}")))));
            }

            foreach (var place in await catalog.GetPlacesAsync(slug, cancellationToken))
            {
                var detail = await catalog.GetPlaceAsync(place.Slug, cancellationToken);
                var languages = detail?.Stories.Where(s => !string.IsNullOrWhiteSpace(s.Text)).Select(s => s.Language).Distinct().Where(settings.Languages.Contains).ToArray() ?? [];
                foreach (var lang in languages)
                {
                    urls.Add(Url(ns, settings.Absolute($"/{lang}/{slug}/{place.Slug}"), Alternates(xhtml, settings, languages.Select(l => (l, $"/{l}/{slug}/{place.Slug}")))));
                }
            }
        }

        // Creator pages (T-1204): every published creator with a validated place in a listed destination, once. The page is the same for each
        // language the creator speaks, so the alternates point to it for each of them.
        Dictionary<string, CreatorSummaryDto> handles = new(StringComparer.OrdinalIgnoreCase);
        foreach (var slug in settings.Destinations)
        {
            foreach (var creator in await TryListAsync(creators, slug, cancellationToken))
            {
                handles.TryAdd(creator.Handle, creator);
            }
        }

        foreach (var handle in handles.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            var page = await creators.GetCreatorAsync(handle, cancellationToken);
            if (page is null)
            {
                continue;
            }

            var languages = page.Languages.Where(settings.Languages.Contains).ToArray();
            urls.Add(Url(ns, settings.Absolute($"/@{page.Handle}"), Alternates(xhtml, settings, languages.Select(l => (l, $"/@{page.Handle}")).Append(("x-default", $"/@{page.Handle}")))));
        }

        urls.Add(Url(ns, settings.Absolute("/micro-aventures")));
        urls.AddRange(content.All.Select(article => Url(ns, settings.Absolute($"/micro-aventures/{article.Path}"))));
        foreach (var legal in LegalPages)
        {
            urls.Add(Url(ns, settings.Absolute($"/fr/{legal}")));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(ns + "urlset", new XAttribute(XNamespace.Xmlns + "xhtml", xhtml), urls)).ToString();
    }

    /// <summary>A failure of Creators must not take the sitemap down: the creator pages are simply left out until it answers.</summary>
    private static async Task<IReadOnlyList<CreatorSummaryDto>> TryListAsync(ICreatorsPublicClient creators, string destination, CancellationToken cancellationToken)
    {
        try
        {
            return await creators.ListCreatorsAsync(destination, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    private static XElement Url(XNamespace ns, string location, IEnumerable<XElement>? alternates = null) =>
        new(ns + "url", new XElement(ns + "loc", location), alternates ?? []);

    private static IEnumerable<XElement> Alternates(XNamespace xhtml, PublicSettings settings, IEnumerable<(string Lang, string Path)> pages) =>
        pages.Select(page => new XElement(xhtml + "link", new XAttribute("rel", "alternate"), new XAttribute("hreflang", page.Lang), new XAttribute("href", settings.Absolute(page.Path))));
}
