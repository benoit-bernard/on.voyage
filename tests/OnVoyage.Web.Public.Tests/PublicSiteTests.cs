using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Web.Public;
using OnVoyage.Web.Public.Seo;

namespace OnVoyage.Web.Public.Tests;

public sealed partial class PublicSiteTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly WebApplicationFactory<PublicSiteMarker> _factory;
    private readonly HttpClient _client;

    private sealed class FakeCatalog : ICatalogPublicClient
    {
        public static readonly PoiSummaryDto Fort = new(Guid.NewGuid(), "fort-saint-jean", "Fort Saint-Jean", "history", 43.2955, 5.3606, 0.8, 0.8, 2, false, null, 90, new Dictionary<string, double>());
        public static readonly PoiSummaryDto Muet = new(Guid.NewGuid(), "sans-texte", "Lieu Premium", "nature", 43.2, 5.3, 0.5, 0.5, 1, false, null, 60, new Dictionary<string, double>());

        public Task<DestinationDto?> GetDestinationAsync(string slug, CancellationToken cancellationToken) =>
            Task.FromResult(slug == "marseille" ? new DestinationDto("marseille", "Marseille", 43.2965, 5.3698, 2) : null);

        public Task<IReadOnlyList<PoiSummaryDto>> GetPlacesAsync(string destination, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PoiSummaryDto>>(destination == "marseille" ? [Fort, Muet] : []);

        public Task<PoiDetailDto?> GetPlaceAsync(string slug, CancellationToken cancellationToken) => Task.FromResult<PoiDetailDto?>(slug switch
        {
            "fort-saint-jean" => new PoiDetailDto(Fort.Id, Fort.Slug, Fort.Name, "history", Fort.Latitude, Fort.Longitude, 0.8, 2, false,
                [new StoryDto(Guid.NewGuid(), "fr", "Le fort du roi", "Louis XIV fait bâtir le fort Saint-Jean à l'entrée du Vieux-Port.\n\nIl surveille la ville autant que la mer.", 90, "https://media/x.mp3", true, new Dictionary<string, string> { ["main"] = "https://media/x.mp3" })],
                ["© OpenStreetMap contributors", "Wikipédia (CC BY-SA)"]),
            "sans-texte" => new PoiDetailDto(Muet.Id, Muet.Slug, Muet.Name, "nature", 43.2, 5.3, 0.5, 1, false,
                [new StoryDto(Guid.NewGuid(), "fr", "Anecdote", string.Empty, 60, null, true)], []),
            _ => null,
        });
    }

    public PublicSiteTests()
    {
        _factory = new WebApplicationFactory<PublicSiteMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:JwtSecret", "tests-secret-0123456789abcdef-0123456789abcdef-0123456789");
            builder.ConfigureServices(services => services.AddSingleton<ICatalogPublicClient, FakeCatalog>());
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<(HttpResponseMessage Response, string Html)> Get(string path, string? userAgent = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (userAgent is not null)
        {
            request.Headers.UserAgent.ParseAdd(userAgent);
        }

        var response = await _client.SendAsync(request, Ct);
        // Razor encodes non-ASCII characters as entities; browsers and crawlers decode them, so the assertions read the decoded text.
        return (response, System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct)));
    }

    [Fact]
    public async Task A_search_engine_gets_the_place_with_the_text_of_its_story()
    {
        var (response, html) = await Get("/fr/marseille/fort-saint-jean", "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Louis XIV fait bâtir le fort Saint-Jean");
        html.ShouldContain("Il surveille la ville autant que la mer.");
        html.ShouldContain("<title>Fort Saint-Jean");
        html.ShouldContain("rel=\"canonical\" href=\"https://on.voyage/fr/marseille/fort-saint-jean\"");
        html.ShouldContain("© OpenStreetMap contributors");
        html.ShouldContain("intelligence artificielle");
    }

    [Theory]
    [InlineData("GPTBot/1.2")]
    [InlineData("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; ClaudeBot/1.0; +claudebot@anthropic.com)")]
    [InlineData("Mozilla/5.0 (compatible; PerplexityBot/1.0)")]
    [InlineData("CCBot/2.0 (https://commoncrawl.org/faq/)")]
    public async Task AI_crawlers_are_refused_whatever_the_page(string userAgent)
    {
        foreach (var path in new[] { "/", "/fr/marseille", "/fr/marseille/fort-saint-jean", "/robots.txt", "/sitemap.xml" })
        {
            (await Get(path, userAgent)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }
    }

    [Fact]
    public async Task The_json_ld_of_a_place_is_valid_and_describes_the_attraction()
    {
        var (_, html) = await Get("/fr/marseille/fort-saint-jean");
        var scripts = JsonLdRegex().Matches(html).Select(m => m.Groups[1].Value).ToArray();
        scripts.ShouldNotBeEmpty();

        using var document = JsonDocument.Parse(scripts[0]);
        var root = document.RootElement;
        root.GetProperty("@context").GetString().ShouldBe("https://schema.org");
        root.GetProperty("@type").GetString().ShouldBe("TouristAttraction");
        root.GetProperty("name").GetString().ShouldBe("Fort Saint-Jean");
        root.GetProperty("geo").GetProperty("latitude").GetDouble().ShouldBe(43.2955, 1e-6);
        root.GetProperty("containedInPlace").GetProperty("@type").GetString().ShouldBe("TouristDestination");
    }

    [Fact]
    public async Task The_destination_page_lists_its_places_and_carries_destination_json_ld()
    {
        var (response, html) = await Get("/fr/marseille");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("/fr/marseille/fort-saint-jean");
        using var document = JsonDocument.Parse(JsonLdRegex().Match(html).Groups[1].Value);
        document.RootElement.GetProperty("@type").GetString().ShouldBe("TouristDestination");
        document.RootElement.GetProperty("includesAttraction").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task A_place_whose_story_has_no_text_does_not_exist_on_the_web()
    {
        // Premium stories come back without text from the Catalog; the web never shows them.
        (await Get("/fr/marseille/sans-texte")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Get("/en/marseille/fort-saint-jean")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound); // no English story
        (await Get("/fr/marseille/inconnu")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Get("/de/marseille")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Get("/fr/atlantis")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task There_is_no_audio_and_no_third_party_request_on_any_page()
    {
        foreach (var path in new[] { "/", "/fr/marseille", "/fr/marseille/fort-saint-jean", "/micro-aventures", "/micro-aventures/eau/gorges-verdon", "/fr/conditions" })
        {
            var (response, html) = await Get(path);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
            html.ShouldNotContain("<audio", Case.Insensitive);
            html.ShouldNotContain(".mp3", Case.Insensitive);
            foreach (var reference in ExternalReferenceRegex().Matches(html).Select(m => m.Groups[1].Value))
            {
                reference.ShouldNotContain("googleapis", Case.Insensitive, path);
                reference.ShouldNotContain("cdn.", Case.Insensitive, path);
                reference.ShouldNotContain("ytimg", Case.Insensitive, path);
            }
        }
    }

    [Fact]
    public async Task Every_html_response_reserves_text_and_data_mining_rights()
    {
        var (response, html) = await Get("/fr/marseille");
        response.Headers.GetValues("tdm-reservation").ShouldBe(["1"]);
        response.Headers.GetValues("tdm-policy").Single().ShouldBe("https://on.voyage/fr/conditions#fouille-de-textes");
        html.ShouldContain("<meta name=\"tdm-reservation\" content=\"1\"");
        html.ShouldContain("<meta name=\"tdm-policy\"");
        (await Get("/fr/conditions")).Html.ShouldContain("article 4 de la directive (UE) 2019/790");
    }

    [Fact]
    public async Task The_tdmrep_file_declares_the_reservation_for_the_whole_site()
    {
        var (response, body) = await Get("/.well-known/tdmrep.json");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(body);
        var entry = document.RootElement[0];
        entry.GetProperty("location").GetString().ShouldBe("/");
        entry.GetProperty("tdm-reservation").GetInt32().ShouldBe(1);
        entry.GetProperty("tdm-policy").GetString()!.ShouldContain("fouille-de-textes");
    }

    [Fact]
    public async Task Robots_txt_allows_the_search_engines_and_forbids_every_ai_crawler_of_the_list()
    {
        var (_, robots) = await Get("/robots.txt");
        foreach (var engine in new[] { "Googlebot", "Bingbot", "Qwantbot", "Applebot", "DuckDuckBot" })
        {
            robots.ShouldContain($"User-agent: {engine}\n".Replace("\n", Environment.NewLine));
        }

        var blocked = _factory.Services.GetRequiredService<BlockedCrawlers>().Tokens;
        blocked.Count.ShouldBeGreaterThanOrEqualTo(26);
        foreach (var token in blocked)
        {
            robots.ShouldContain($"User-agent: {token}");
        }

        var section = robots[robots.IndexOf("User-agent: GPTBot", StringComparison.Ordinal)..];
        section[..section.IndexOf("Disallow: /", StringComparison.Ordinal)].ShouldContain("User-agent: ClaudeBot");
        robots.ShouldContain("Sitemap: https://on.voyage/sitemap.xml");
        robots.ShouldContain("Disallow: /api/");
    }

    [Fact]
    public async Task The_sitemap_lists_pages_with_hreflang_and_leaves_out_places_without_text()
    {
        var (response, xml) = await Get("/sitemap.xml");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        xml.ShouldContain("<loc>https://on.voyage/fr/marseille/fort-saint-jean</loc>");
        xml.ShouldContain("hreflang=\"fr\"");
        xml.ShouldNotContain("sans-texte");
        xml.ShouldNotContain("/en/marseille/fort-saint-jean");
        xml.ShouldContain("<loc>https://on.voyage/micro-aventures/eau/gorges-verdon</loc>");
        xml.ShouldContain("<loc>https://on.voyage/en/marseille</loc>");
    }

    [Fact]
    public async Task Every_address_of_the_former_blog_answers_200_or_301_to_a_page_that_answers_200()
    {
        foreach (var (old, target) in ContentStore.LegacyRedirects)
        {
            var (response, _) = await Get(old);
            response.StatusCode.ShouldBe(HttpStatusCode.MovedPermanently, old);
            response.Headers.Location!.OriginalString.ShouldBe(target);
            (await Get(target)).Response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{old} → {target}");
        }

        ContentStore.LegacyRedirects.Count.ShouldBe(9);
    }

    [Fact]
    public async Task Old_articles_keep_their_text_and_their_links_point_to_the_new_addresses()
    {
        var (response, html) = await Get("/micro-aventures/eau/gorges-verdon");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Les Gorges du Verdon en paddle");
        html.ShouldContain("Départ à l'aube");
        html.ShouldNotContain(".html\"");
        html.ShouldContain("href=\"/micro-aventures/eau/descente-ardeche\"");
        (await Get("/micro-aventures/eau")).Html.ShouldContain("/micro-aventures/eau/allier-bivouac");
        (await Get("/micro-aventures/terre")).Html.ShouldContain("/micro-aventures/blog/silence-arctique");
        (await Get("/micro-aventures/eau/inconnu")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Get("/micro-aventures/ciel")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Universal_link_files_are_served_as_json()
    {
        var (aasa, body) = await Get("/.well-known/apple-app-site-association");
        aasa.StatusCode.ShouldBe(HttpStatusCode.OK);
        aasa.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        JsonDocument.Parse(body).RootElement.GetProperty("applinks").GetProperty("details")[0].GetProperty("appID").GetString().ShouldNotBeNullOrEmpty();
        JsonDocument.Parse((await Get("/.well-known/assetlinks.json")).Html).RootElement[0].GetProperty("target").GetProperty("namespace").GetString().ShouldBe("android_app");
    }

    [Fact]
    public async Task The_home_page_answers_quickly_with_the_value_proposition_and_the_store_links()
    {
        var (response, html) = await Get("/");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Les lieux ont des histoires");
        html.ShouldContain("Marseille");
        html.ShouldContain("App Store");
        html.ShouldNotContain("nosnippet", Case.Insensitive); // Q-07: no snippet restriction by default
    }

    [GeneratedRegex("<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex JsonLdRegex();

    [GeneratedRegex("(?:src|href)=\"(https?://[^\"]+)\"")]
    private static partial Regex ExternalReferenceRegex();
}
