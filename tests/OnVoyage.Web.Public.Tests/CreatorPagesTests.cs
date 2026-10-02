using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using OnVoyage.Creators.Contracts;
using OnVoyage.Web.Public.Seo;

namespace OnVoyage.Web.Public.Tests;

public sealed partial class PublicSiteTests
{
    /// <summary>
    /// Stands for the Creators API, which already filters to what is public (published creator, validated links, online contents, published places;
    /// proved against the database in Creators.IntegrationTests). Anything it does not return cannot be on a page.
    /// </summary>
    private sealed class FakeCreators : ICreatorsPublicClient
    {
        public static readonly Guid MarieId = Guid.NewGuid();
        public static bool Down { get; set; }

        public static CreatorSummaryDto MarieSummary { get; } = new(MarieId, "marie", "Marie Dupont", "avatars/marie.jpg", ["history"], 1);

        private static readonly CreatorPageDto Marie = new(
            MarieId, "marie", "Marie Dupont", "Historienne du Sud, je raconte les forts & les ports.", "avatars/marie.jpg", ["fr", "en"], ["history"],
            [new CreatorLinkDto("youtube", "https://www.youtube.com/@marie"), new CreatorLinkDto("instagram", "https://www.instagram.com/marie")],
            null, true, 1, 1, false,
            [new CreatorPlaceDto(FakeCatalog.Fort.Id, "Fort Saint-Jean", "Marseille", "Montez au coucher du soleil.",
                [new CreatorContentDto(Guid.NewGuid(), "youtube", "video", "Le fort en 90 secondes", "https://www.youtube.com/watch?v=abcdefghijk&t=30s", 30, 90, null, true, null)])],
            []);

        public Task<CreatorPageDto?> GetCreatorAsync(string handle, CancellationToken cancellationToken) =>
            Task.FromResult(string.Equals(handle, "marie", StringComparison.OrdinalIgnoreCase) ? Marie : null);

        public Task<PoiCreatorsDto?> GetPoiCreatorsAsync(Guid poiId, int limit, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new HttpRequestException("creators down");
            }

            return Task.FromResult<PoiCreatorsDto?>(poiId == FakeCatalog.Fort.Id
                ? new PoiCreatorsDto(poiId, 1, [new PoiCreatorItemDto(MarieSummary, "Montez au coucher du soleil.", Marie.Places[0].Contents[0])])
                : new PoiCreatorsDto(poiId, 0, []));
        }

        public Task<IReadOnlyList<CreatorSummaryDto>> ListCreatorsAsync(string destination, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CreatorSummaryDto>>(destination == "marseille" ? [MarieSummary] : []);
    }

    [Fact]
    public async Task A_creator_page_shows_the_profile_and_its_validated_places_with_links_out_that_pass_no_authority()
    {
        var (response, html) = await Get("/@marie", "Mozilla/5.0 (compatible; Googlebot/2.1)");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("<h1>Marie Dupont</h1>");
        html.ShouldContain("@marie");
        html.ShouldContain("Nouveau créateur");
        html.ShouldContain("Historienne du Sud");
        html.ShouldContain("href=\"/fr/marseille/fort-saint-jean\">Fort Saint-Jean");
        html.ShouldContain("Montez au coucher du soleil.");
        html.ShouldContain("href=\"https://www.youtube.com/watch?v=abcdefghijk&t=30s\"");
        html.ShouldContain("Publicité");
        html.ShouldContain("rel=\"canonical\" href=\"https://on.voyage/@marie\"");
        html.ShouldNotContain("<audio", Case.Insensitive);
        html.ShouldNotContain("<iframe", Case.Insensitive);
        var main = html[html.IndexOf("<main", StringComparison.Ordinal)..html.IndexOf("</main>", StringComparison.Ordinal)];
        foreach (var anchor in OutboundAnchorRegex().Matches(main).Select(m => m.Value))
        {
            anchor.ShouldContain("rel=\"");
            anchor.ShouldContain("noopener");
            anchor.ShouldContain("nofollow");
        }
    }

    [Fact]
    public async Task A_creator_page_links_out_to_nothing_but_the_creators_own_links_and_validated_contents()
    {
        var (_, html) = await Get("/@marie");

        var main = html[html.IndexOf("<main", StringComparison.Ordinal)..html.IndexOf("</main>", StringComparison.Ordinal)];
        var outbound = OutboundAnchorRegex().Matches(main).Select(m => System.Text.RegularExpressions.Regex.Match(m.Value, "href=\"([^\"]+)\"").Groups[1].Value).ToArray();

        outbound.ShouldBe(
            ["https://www.youtube.com/@marie", "https://www.instagram.com/marie", "https://www.youtube.com/watch?v=abcdefghijk&t=30s"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task The_creator_json_ld_is_a_person_with_the_public_links_as_same_as()
    {
        var (_, html) = await Get("/@marie");
        var scripts = JsonLdRegexForCreators().Matches(html).Select(m => m.Groups[1].Value).ToArray();
        scripts.ShouldHaveSingleItem();

        using var document = JsonDocument.Parse(scripts[0]);
        var root = document.RootElement;
        root.GetProperty("@context").GetString().ShouldBe("https://schema.org");
        root.GetProperty("@type").GetString().ShouldBe("Person");
        root.GetProperty("name").GetString().ShouldBe("Marie Dupont");
        root.GetProperty("url").GetString().ShouldBe("https://on.voyage/@marie");
        root.GetProperty("image").GetString().ShouldBe("https://on.voyage/media/avatars/marie.jpg");
        root.GetProperty("sameAs").EnumerateArray().Select(e => e.GetString()).ShouldBe(["https://www.youtube.com/@marie", "https://www.instagram.com/marie"]);
        root.GetProperty("knowsAbout")[0].GetString().ShouldBe("Histoire");
    }

    [Fact]
    public async Task The_creator_page_declares_its_languages_with_hreflang_and_a_default()
    {
        var (_, html) = await Get("/@marie");

        html.ShouldContain("<link rel=\"alternate\" hreflang=\"fr\" href=\"https://on.voyage/@marie\"");
        html.ShouldContain("<link rel=\"alternate\" hreflang=\"en\" href=\"https://on.voyage/@marie\"");
        html.ShouldContain("<link rel=\"alternate\" hreflang=\"x-default\" href=\"https://on.voyage/@marie\"");
    }

    [Fact]
    public async Task An_unknown_or_unpublished_handle_does_not_exist_and_crawlers_of_ai_are_refused()
    {
        (await Get("/@personne")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Get("/@marie", "GPTBot/1.2")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_place_page_carries_the_creators_block_with_a_link_to_the_creator_page()
    {
        var (response, html) = await Get("/fr/marseille/fort-saint-jean");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Vu par les créateurs");
        html.ShouldContain("href=\"/@marie\">@marie");
        html.ShouldContain("Montez au coucher du soleil.");
        html.ShouldContain("Publicité");
        html.ShouldContain("href=\"https://www.youtube.com/watch?v=abcdefghijk&t=30s\"");
        html.ShouldContain("target=\"_blank\" rel=\"nofollow noopener noreferrer\"");
    }

    [Fact]
    public async Task A_place_without_creators_has_no_block_and_a_failure_of_creators_does_not_break_the_page()
    {
        FakeCreators.Down = true;
        try
        {
            var (response, html) = await Get("/fr/marseille/fort-saint-jean");

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            html.ShouldContain("Louis XIV fait bâtir le fort Saint-Jean");
            html.ShouldNotContain("Vu par les créateurs");
        }
        finally
        {
            FakeCreators.Down = false;
        }
    }

    [Fact]
    public async Task The_sitemap_lists_the_creator_page_with_its_alternates()
    {
        var (_, xml) = await Get("/sitemap.xml");

        xml.ShouldContain("<loc>https://on.voyage/@marie</loc>");
        xml.ShouldContain("hreflang=\"x-default\" href=\"https://on.voyage/@marie\"");
        xml.ShouldContain("hreflang=\"en\" href=\"https://on.voyage/@marie\"");
    }

    [Fact]
    public async Task The_real_client_presents_an_internal_token_caches_and_pages_through_the_list()
    {
        var calls = new List<(string Url, string? Authorization)>();
        var handler = new RecordingHandler(request =>
        {
            calls.Add((request.RequestUri!.PathAndQuery, request.Headers.Authorization?.Scheme));
            return request.RequestUri.AbsolutePath switch
            {
                "/api/creators/v1/creators/ghost" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/creators/v1/creators" when request.RequestUri.Query.Contains("cursor=50", StringComparison.Ordinal) =>
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new CreatorListDto([new CreatorSummaryDto(Guid.NewGuid(), "second", "Second", null, [], 1)], null)) },
                "/api/creators/v1/creators" =>
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new CreatorListDto([new CreatorSummaryDto(Guid.NewGuid(), "first", "First", null, [], 1)], "50")) },
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            };
        });
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var client = new CreatorsPublicClient(new HttpClient(handler) { BaseAddress = new Uri("http://creators/") }, memory);

        (await client.GetCreatorAsync("ghost", Ct)).ShouldBeNull();
        (await client.GetCreatorAsync("ghost", Ct)).ShouldBeNull();
        (await client.ListCreatorsAsync("marseille", Ct)).Select(c => c.Handle).ShouldBe(["first", "second"]);

        calls.Count(c => c.Url.EndsWith("/ghost", StringComparison.Ordinal)).ShouldBe(1); // the second answer came from the cache
        calls.Count.ShouldBe(3);
        await Should.ThrowAsync<HttpRequestException>(() => client.GetPoiCreatorsAsync(Guid.NewGuid(), 10, Ct));
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    [System.Text.RegularExpressions.GeneratedRegex("<a [^>]*href=\"https?://(?!on\\.voyage)[^\"]+\"[^>]*>")]
    private static partial System.Text.RegularExpressions.Regex OutboundAnchorRegex();

    [System.Text.RegularExpressions.GeneratedRegex("<script type=\"application/ld\\+json\">(.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex JsonLdRegexForCreators();
}
