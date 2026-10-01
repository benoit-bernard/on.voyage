using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OnVoyage.Factory.Infrastructure.Sources;

namespace Factory.UnitTests;

/// <summary>Wikimedia is not reachable from the build environment, so these pin the request and response shapes the clients rely on.</summary>
public sealed class SourcesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ScriptedHandler(Queue<Func<HttpRequestMessage, HttpResponseMessage>> script) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(script.Dequeue()(request));
        }
    }

    private static (WikimediaRequester Requester, ScriptedHandler Handler) Requester(params Func<HttpRequestMessage, HttpResponseMessage>[] steps)
    {
        var handler = new ScriptedHandler(new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(steps));
        return (new WikimediaRequester(new HttpClient(handler), TimeProvider.System, NullLogger<WikimediaRequester>.Instance) { FirstDelay = TimeSpan.FromMilliseconds(1) }, handler);
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    // ---- etiquette

    [Fact]
    public async Task Every_call_carries_the_mandatory_user_agent_and_the_accept_header()
    {
        var (requester, handler) = Requester(_ => Ok("{}"));

        await requester.GetStringAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://example.org/x"), "application/json", Ct);

        handler.Requests.Single().Headers.UserAgent.ToString().ShouldBe("OnVoyageBot/1.0 (https://on.voyage/bot; contact@on.voyage)");
        handler.Requests.Single().Headers.Accept.ToString().ShouldBe("application/json");
    }

    [Fact]
    public async Task Too_many_requests_back_off_and_retry_then_succeed()
    {
        var (requester, handler) = Requester(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests), _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests), _ => Ok("done"));

        (await requester.GetStringAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://example.org/x"), "application/json", Ct)).ShouldBe("done");
        handler.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Retry_after_is_honoured()
    {
        var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
        var (requester, handler) = Requester(_ => limited, _ => Ok("ok"));

        await requester.GetStringAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://example.org/x"), "application/json", Ct);

        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task After_the_last_attempt_the_failure_is_reported_and_a_404_is_just_absence()
    {
        var (giveUp, _) = Requester(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests), _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests), _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests), _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests), _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        await Should.ThrowAsync<HttpRequestException>(() => giveUp.GetStringAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://example.org/x"), "application/json", Ct));

        var (missing, _) = Requester(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        (await missing.GetStringAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://example.org/x"), "application/json", Ct)).ShouldBeNull();

        var (broken, _) = Requester(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Should.ThrowAsync<HttpRequestException>(() => broken.GetStringAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://example.org/x"), "application/json", Ct));
    }

    [Fact]
    public async Task Requests_never_overlap()
    {
        var running = 0;
        var maximum = 0;
        var handler = new ConcurrencyProbe(() =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref running));
            Thread.Sleep(20);
            Interlocked.Decrement(ref running);
        });
        var requester = new WikimediaRequester(new HttpClient(handler), TimeProvider.System, NullLogger<WikimediaRequester>.Instance);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => requester.GetStringAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://example.org/x"), "application/json", Ct)));

        maximum.ShouldBe(1);
    }

    private sealed class ConcurrencyProbe(Action work) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            work();
            return Task.FromResult(Ok("{}"));
        }
    }

    // ---- Wikidata

    [Fact]
    public void The_query_lists_only_well_formed_qids_in_one_values_clause()
    {
        var query = WikidataSparqlClient.BuildQuery(["Q1", "Q273003"]);

        query.ShouldContain("VALUES ?item { wd:Q1 wd:Q273003 }");
        foreach (var property in new[] { "wdt:P31", "wdt:P279", "wdt:P1435", "wdt:P571", "wdt:P18", "wdt:P856", "wikibase:sitelinks", "fr.wikipedia.org", "en.wikipedia.org" })
        {
            query.ShouldContain(property);
        }

        WikidataSparqlClient.IsQid("Q42").ShouldBeTrue();
        WikidataSparqlClient.IsQid("q42").ShouldBeTrue();
        foreach (var bad in new[] { "", "Q", "42", "Q4 2", "Q1}", "wd:Q1", "Q1; DROP", "Q123456789012345678" })
        {
            WikidataSparqlClient.IsQid(bad).ShouldBeFalse(bad);
        }
    }

    [Fact]
    public async Task Malformed_qids_never_reach_the_endpoint()
    {
        var (requester, handler) = Requester();
        var client = new WikidataSparqlClient(requester, TimeProvider.System);

        (await client.GetEntitiesAsync(["Q1}", "nope"], Ct)).ShouldBeEmpty();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void Results_in_long_format_are_folded_into_one_enrichment_per_entity()
    {
        const string json = """
            {"head":{"vars":["item","kind","value"]},"results":{"bindings":[
              {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q273003"},"kind":{"type":"literal","value":"label_fr"},"value":{"type":"literal","value":"Notre-Dame de la Garde"}},
              {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q273003"},"kind":{"type":"literal","value":"label_en"},"value":{"type":"literal","value":"Notre-Dame de la Garde"}},
              {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q273003"},"kind":{"type":"literal","value":"description_fr"},"value":{"type":"literal","value":"basilique de Marseille"}},
              {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q273003"},"kind":{"type":"literal","value":"instance_of"},"value":{"type":"literal","value":"http://www.wikidata.org/entity/Q16970"}},
              {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q273003"},"kind":{"type":"literal","value":"instance_of"},"value":{"type":"literal","value":"http://www.wikidata.org/entity/Q41176"}},
              {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q273003"},"kind":{"type":"literal","value":"instance_of"},"value":{"type":"literal","value":"http://www.wikidata.org/entity/Q16970"}},
              {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q273003"},"kind":{"type":"literal","value":"heritage"},"value":{"type":"literal","value":"http://www.wikidata.org/entity/Q10387689"}},
              {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q273003"},"kind":{"type":"literal","value":"sitelinks"},"value":{"type":"literal","value":"57"}},
              {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q273003"},"kind":{"type":"literal","value":"wikipedia_fr"},"value":{"type":"literal","value":"Basilique Notre-Dame-de-la-Garde"}},
              {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q273003"},"kind":{"type":"literal","value":"inception"},"value":{"type":"literal","value":"1864-01-01T00:00:00Z"}},
              {"item":{"type":"uri","value":"http://www.wikidata.org/entity/Q1"},"kind":{"type":"literal","value":"label_fr"},"value":{"type":"literal","value":"Univers"}}
            ]}}
            """;

        var entities = WikidataSparqlClient.Parse(json, DateTimeOffset.UnixEpoch);

        entities.Count.ShouldBe(2);
        var garde = entities.Single(entity => entity.Qid == "Q273003");
        garde.LabelFr.ShouldBe("Notre-Dame de la Garde");
        garde.DescriptionFr.ShouldBe("basilique de Marseille");
        garde.DescriptionEn.ShouldBeNull();
        garde.InstanceOf.ShouldBe(["Q16970", "Q41176"]);
        garde.HeritageStatuses.ShouldBe(["Q10387689"]);
        garde.Sitelinks.ShouldBe(57);
        garde.WikipediaFr.ShouldBe("Basilique Notre-Dame-de-la-Garde");
        garde.WikipediaEn.ShouldBeNull();
        garde.Inception.ShouldStartWith("1864");
        entities.Single(entity => entity.Qid == "Q1").Sitelinks.ShouldBe(0);
    }

    [Fact]
    public void An_empty_result_is_an_empty_list() =>
        WikidataSparqlClient.Parse("""{"results":{"bindings":[]}}""", DateTimeOffset.UnixEpoch).ShouldBeEmpty();

    // ---- page views

    [Fact]
    public void The_window_is_the_twelve_full_months_before_the_current_one()
    {
        WikimediaPageviewsClient.Window(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero)).ShouldBe(("2025100100", "2026090100"));
        WikimediaPageviewsClient.Window(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)).ShouldBe(("2025010100", "2025120100"));
    }

    [Fact]
    public void Monthly_views_are_summed_and_a_missing_series_is_zero()
    {
        WikimediaPageviewsClient.SumViews("""{"items":[{"article":"A","timestamp":"2025100100","views":1200},{"article":"A","timestamp":"2025110100","views":800}]}""").ShouldBe(2000);
        WikimediaPageviewsClient.SumViews("""{"items":[]}""").ShouldBe(0);
        WikimediaPageviewsClient.SumViews("""{"type":"x"}""").ShouldBe(0);
    }

    [Fact]
    public async Task The_request_targets_the_article_of_the_right_wiki_with_a_url_safe_title()
    {
        var (requester, handler) = Requester(_ => Ok("""{"items":[{"views":10}]}"""));
        var client = new WikimediaPageviewsClient(requester, new FixedClock());

        (await client.GetAnnualViewsAsync("fr", "Château d'If (Marseille)", Ct)).ShouldBe(10);

        var url = handler.Requests.Single().RequestUri!.AbsoluteUri;
        url.ShouldStartWith("https://wikimedia.org/api/rest_v1/metrics/pageviews/per-article/fr.wikipedia/all-access/user/");
        url.ShouldContain("Ch%C3%A2teau_d'If_(Marseille)".Replace("'", "%27").Replace("(", "%28").Replace(")", "%29"));
        url.ShouldEndWith("/monthly/2025100100/2026090100");
        (await client.GetAnnualViewsAsync("de", "X", Ct)).ShouldBe(0);
        (await client.GetAnnualViewsAsync("fr", " ", Ct)).ShouldBe(0);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    }

    // ---- configuration

    [Fact]
    public async Task Destinations_come_from_configuration_with_a_mandatory_bounding_box()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Factory:Destinations:0:Slug"] = "marseille",
            ["Factory:Destinations:0:Name"] = "Marseille",
            ["Factory:Destinations:0:CenterLatitude"] = "43.2965",
            ["Factory:Destinations:0:CenterLongitude"] = "5.3698",
            ["Factory:Destinations:0:BoundingBox:0"] = "5.2",
            ["Factory:Destinations:0:BoundingBox:1"] = "43.1",
            ["Factory:Destinations:0:BoundingBox:2"] = "5.5",
            ["Factory:Destinations:0:BoundingBox:3"] = "43.4",
            ["Factory:Destinations:1:Slug"] = "broken",
            ["Factory:Destinations:1:BoundingBox:0"] = "1",
        }).Build();
        var catalog = new ConfiguredDestinationCatalog(configuration);

        var marseille = await catalog.FindAsync("MARSEILLE", Ct);
        marseille!.MinLongitude.ShouldBe(5.2);
        marseille.MaxLatitude.ShouldBe(43.4);
        marseille.OsmExtractUrl.ShouldContain("provence-alpes-cote-d-azur");
        (await catalog.FindAsync("atlantis", Ct)).ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => catalog.FindAsync("broken", Ct));
    }

    [Fact]
    public void The_embedded_rule_set_is_the_shipped_mapping_file()
    {
        var provider = new JsonClassificationRuleProvider(new ConfigurationBuilder().Build());

        provider.Current.Version.ShouldBe(1);
        provider.Current.Rules["historic=fort"].Select(rule => rule.Code).ShouldContain("history.military");
        provider.Current.Rules.Count.ShouldBeGreaterThan(40);
    }
}
