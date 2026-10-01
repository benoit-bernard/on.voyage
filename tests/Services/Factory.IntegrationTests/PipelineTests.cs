using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnVoyage.Factory.Application;
using OnVoyage.TestInfrastructure;

namespace Factory.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class PipelineTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private FactoryHarness _f = null!;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(FactoryHarness.Osm2pgsqlAvailable, "osm2pgsql is not installed (apt install osm2pgsql).");
        _f = await FactoryHarness.StartAsync(postgres);
        Seed();
    }

    public async ValueTask DisposeAsync()
    {
        if (_f is not null)
        {
            await _f.DisposeAsync();
        }
    }

    private static PlaceEnrichment Entity(string qid, string labelFr, int sitelinks, string[] heritage, string[] classes, string? wikipedia) =>
        new(qid, labelFr, null, $"Description de {labelFr}", null, classes, heritage, null, sitelinks, wikipedia, null, null, null, DateTimeOffset.UtcNow);

    private void Seed()
    {
        _f.Wikidata.Entities["Q1457372"] = Entity("Q1457372", "Fort Saint-Jean", 40, ["Q10387689"], ["Q23413"], "Fort Saint-Jean (Marseille)");
        _f.Wikidata.Entities["Q273003"] = Entity("Q273003", "Notre-Dame de la Garde", 80, ["Q10387689"], [], "Basilique Notre-Dame de la Garde");
        _f.Wikidata.Entities["Q1075331"] = Entity("Q1075331", "Abbaye Saint-Victor de Marseille", 25, ["Q10387689"], ["Q16970"], "Abbaye Saint-Victor de Marseille");
        _f.Pageviews.ByTitle["Fort Saint-Jean (Marseille)"] = 90_000;
        _f.Pageviews.ByTitle["Basilique Notre-Dame de la Garde"] = 2_000_000;
        _f.Pageviews.ByTitle["Abbaye Saint-Victor de Marseille"] = 3_000;
    }

    private static string Url(Guid id, string action) => $"/api/factory/v1/admin/places/{id}/{action}";

    private async Task<Guid> IdOfAsync(string slug) => (await _f.GetPlaceAsync(slug)).GetProperty("id").GetGuid();

    [Fact]
    public async Task The_whole_chain_imports_dedups_enriches_classifies_and_scores_the_sample_extract()
    {
        await _f.RunPipelineAsync();

        // 10 named objects survive the osm2pgsql filter; the duplicate node of the fort is folded into the fort.
        (await _f.CountAsync("select count(*) from factory.place")).ShouldBe(10);
        (await _f.CountAsync("select count(*) from factory.place where status = 'Merged'")).ShouldBe(1);
        (await _f.CountAsync("select count(*) from factory.import_run where raw_rows = 10")).ShouldBe(1);

        var link = await _f.QueryAsync("select reason, automatic, similarity from factory.dedup_link", r => (r.GetString(0), r.GetBoolean(1), r.GetDouble(2)));
        link.Item1.ShouldBe("close_and_same_name");
        link.Item2.ShouldBeTrue();
        (await _f.QueryAsync("select p.slug from factory.dedup_link l join factory.place p on p.id = l.kept_place_id", r => r.GetString(0))).ShouldBe("fort-saint-jean");

        // The museum inside the fort is a different place, not a duplicate.
        (await _f.GetPlaceAsync("musee-d-histoire-dans-le-fort")).GetProperty("status").GetString().ShouldBe("Candidate");

        // Notre-Dame de la Garde: classified heritage 30 + sitelinks capped 25 + page views capped 20 = 75.
        var garde = await _f.GetPlaceAsync("notre-dame-de-la-garde");
        garde.GetProperty("importanceScore").GetInt32().ShouldBe(75);
        garde.GetProperty("qid").GetString().ShouldBe("Q273003");
        garde.GetProperty("annualPageviews").GetInt64().ShouldBe(2_000_000);
        garde.GetProperty("classification").GetString().ShouldBe("Rules");

        // Rules turn tags and Wikidata classes into weights; level-1 weights are derived.
        var fortId = await IdOfAsync("fort-saint-jean");
        var fort = (await _f.Admin.GetFromJsonAsync<JsonElement>($"/api/factory/v1/admin/places/{fortId}", Ct)).GetProperty("interests").EnumerateArray()
            .ToDictionary(item => item.GetProperty("code").GetString()!, item => item.GetProperty("weight").GetDouble());
        fort["history.military"].ShouldBe(0.9, 1e-6);
        fort["architecture.defensive"].ShouldBe(0.9, 1e-6);
        fort["history"].ShouldBe(0.9, 1e-6);

        // The beach peaks one level above its base (percentile 0 → base 1).
        (await _f.QueryAsync("select crowd_shoulder, crowd_peak from factory.place where slug = 'plage-des-catalans'", r => (r.GetInt16(0), r.GetInt16(1)))).ShouldBe(((short)1, (short)2));

        // Nothing in the rules covers "historic=yes": a person decides, not the pipeline.
        (await _f.GetPlaceAsync("lieu-mysterieux")).GetProperty("status").GetString().ShouldBe("NeedsReview");
        (await _f.CountAsync("select count(*) from factory.place_interest i join factory.place p on p.id = i.place_id where p.slug = 'lieu-mysterieux'")).ShouldBe(0);

        // Provenance (§7.8) on open data, kept apart from proprietary scores.
        (await _f.QueryAsync("select source, source_license, source_url from factory.place where slug = 'fort-saint-jean'", r => (r.GetString(0), r.GetString(1), r.GetString(2))))
            .ShouldBe(("osm", "ODbL-1.0", "https://www.openstreetmap.org/way/2001"));
        (await _f.QueryAsync("select source_license, source_url from factory_raw.wikidata_entity where qid = 'Q273003'", r => (r.GetString(0), r.GetString(1))))
            .ShouldBe(("CC0-1.0", "https://www.wikidata.org/wiki/Q273003"));
    }

    [Fact]
    public async Task Enrichment_asks_wikidata_once_per_batch_and_only_for_places_with_a_qid()
    {
        await _f.RunPipelineAsync();

        _f.Wikidata.Requests.SelectMany(batch => batch).Order().ShouldBe(["Q1075331", "Q1457372", "Q273003", "Q9999999"]);
    }

    [Fact]
    public async Task Running_the_import_again_creates_nothing_and_keeps_editorial_decisions()
    {
        await _f.RunPipelineAsync();
        var garde = await IdOfAsync("notre-dame-de-la-garde");
        (await _f.Admin.PutAsJsonAsync($"/api/factory/v1/admin/places/{garde}/editorial", new { importanceOverride = 33, editoriallySaturated = true }, Ct)).EnsureSuccessStatusCode();

        (await _f.Admin.PostAsJsonAsync("/api/factory/v1/admin/imports", new { destination = "marseille" }, Ct)).EnsureSuccessStatusCode();
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select count(*) from factory.import_run") == 2 && await _f.CountAsync("select count(*) from factory.place where importance_score = 33") == 1)).ShouldBeTrue();

        (await _f.CountAsync("select count(*) from factory.place")).ShouldBe(10);
        (await _f.CountAsync("select count(*) from factory.dedup_link")).ShouldBe(1);
        // Saturated sites sit at the top of the crowd scale and the override replaces the computed importance.
        (await _f.QueryAsync("select importance_score, crowd_shoulder, crowd_peak from factory.place where slug = 'notre-dame-de-la-garde'", r => (r.GetInt16(0), r.GetInt16(1), r.GetInt16(2))))
            .ShouldBe(((short)33, (short)5, (short)5));
    }

    [Fact]
    public async Task A_reverted_merge_brings_the_place_back_and_is_not_redone_by_the_next_scoring()
    {
        await _f.RunPipelineAsync();
        var linkId = await _f.QueryAsync("select id from factory.dedup_link", r => r.GetGuid(0));

        (await _f.Admin.PostAsync($"/api/factory/v1/admin/dedup/{linkId}/revert", null, Ct)).EnsureSuccessStatusCode();
        (await _f.CountAsync("select count(*) from factory.place where status = 'Merged'")).ShouldBe(0);

        (await _f.Admin.PostAsJsonAsync("/api/factory/v1/admin/scorings", new { destination = "marseille" }, Ct)).EnsureSuccessStatusCode();
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select count(*) from factory.place where importance_score is not null") == 10)).ShouldBeTrue();
        (await _f.CountAsync("select count(*) from factory.place where status = 'Merged'")).ShouldBe(0);
        (await _f.Admin.PostAsync($"/api/factory/v1/admin/dedup/{linkId}/revert", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Publishing_projects_the_place_into_the_catalog_and_unpublishing_removes_it()
    {
        await _f.RunPipelineAsync();
        var garde = await IdOfAsync("notre-dame-de-la-garde");

        var published = await _f.Admin.PostAsync(Url(garde, "publish"), null, Ct);
        published.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await published.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetInt32().ShouldBe(1);

        (await FactoryHarness.EventuallyAsync(async () => (await _f.Traveler.GetAsync("/api/catalog/v1/pois/notre-dame-de-la-garde", Ct)).StatusCode == HttpStatusCode.OK)).ShouldBeTrue("the event never reached the catalog");

        var pois = await _f.Traveler.GetFromJsonAsync<JsonElement>("/api/catalog/v1/destinations/marseille/pois", Ct);
        var projected = pois.EnumerateArray().Single();
        projected.GetProperty("name").GetString().ShouldBe("Notre-Dame de la Garde");
        projected.GetProperty("importance").GetDouble().ShouldBe(0.75, 1e-6);
        projected.GetProperty("weights").GetProperty("nature.viewpoints").GetDouble().ShouldBe(0.9, 1e-6);
        (await _f.Traveler.GetFromJsonAsync<JsonElement>("/api/catalog/v1/destinations/marseille", Ct)).GetProperty("poiCount").GetInt32().ShouldBe(1);

        // Republishing is a new version and does not duplicate anything.
        (await _f.Admin.PostAsync(Url(garde, "publish"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select version from catalog.poi") == 2)).ShouldBeTrue();
        (await _f.CountAsync("select count(*) from catalog.poi")).ShouldBe(1);

        var unpublished = await _f.Admin.PostAsJsonAsync(Url(garde, "unpublish"), new { reason = "Travaux de relecture" }, Ct);
        unpublished.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryHarness.EventuallyAsync(async () => (await _f.Traveler.GetAsync("/api/catalog/v1/pois/notre-dame-de-la-garde", Ct)).StatusCode == HttpStatusCode.NotFound)).ShouldBeTrue("the place stayed visible");
        (await _f.Admin.PostAsJsonAsync(Url(garde, "unpublish"), new { reason = "again" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Places_that_need_review_or_were_merged_cannot_be_published_and_a_reason_is_required_to_unpublish()
    {
        await _f.RunPipelineAsync();
        var mystery = await IdOfAsync("lieu-mysterieux");
        var mergedId = await _f.QueryAsync("select other_place_id from factory.dedup_link", r => r.GetGuid(0));

        var review = await _f.Admin.PostAsync(Url(mystery, "publish"), null, Ct);
        review.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await review.Content.ReadAsStringAsync(Ct)).ShouldContain("place_not_publishable");
        (await _f.Admin.PostAsync(Url(mergedId, "publish"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _f.Admin.PostAsync(Url(Guid.NewGuid(), "publish"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var beach = await IdOfAsync("plage-des-catalans");
        await _f.Admin.PostAsync(Url(beach, "publish"), null, Ct);
        (await _f.Admin.PostAsJsonAsync(Url(beach, "unpublish"), new { reason = "  " }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _f.Admin.PostAsync(Url(beach, "reject"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_catalog_slug_taken_by_another_place_does_not_block_a_publication()
    {
        await _f.RunPipelineAsync();
        await _f.ExecuteAsync("""
            insert into catalog.destination (id, slug, name_fr, center, is_active, sort_order, created_at) values ('0192a000-0000-7000-8000-0000000000aa', 'marseille', 'Marseille', ST_GeogFromText('POINT(5.37 43.29)'), true, 0, now());
            insert into catalog.poi (id, destination_id, slug, location, importance_score, hidden_gem, content_quality_score, taxonomy_version, version, created_at)
            values ('0192a000-0000-7000-8000-0000000000bb', '0192a000-0000-7000-8000-0000000000aa', 'plage-des-catalans', ST_GeogFromText('POINT(5.35 43.28)'), 10, false, 0.5, 1, 1, now());
            """);
        var beach = await IdOfAsync("plage-des-catalans");

        (await _f.Admin.PostAsync(Url(beach, "publish"), null, Ct)).EnsureSuccessStatusCode();

        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select count(*) from catalog.poi") == 2)).ShouldBeTrue();
        (await _f.QueryAsync("select slug from catalog.poi where id = '" + beach + "'", r => r.GetString(0))).ShouldStartWith("plage-des-catalans-");
    }

    [Fact]
    public async Task The_admin_api_needs_an_admin_token()
    {
        const string url = "/api/factory/v1/admin/places?destination=marseille";
        using var anonymous = _f.ApiClient();
        using var traveler = _f.ApiClient(TestTokens.Mint());
        using var verifiedNonAdmin = _f.ApiClient(TestTokens.Mint(anonymous: false));
        using var forged = _f.ApiClient(TestTokens.Mint(roles: ["admin"], secret: "an-attacker-secret-0123456789abcdef-0123456789"));

        (await anonymous.GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await forged.GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await traveler.GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await verifiedNonAdmin.GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await traveler.PostAsJsonAsync("/api/factory/v1/admin/imports", new { destination = "marseille" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _f.Admin.GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
