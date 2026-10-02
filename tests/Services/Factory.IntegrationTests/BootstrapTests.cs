using System.Diagnostics;
using System.Net.Http.Json;
using OnVoyage.Factory.Application.Features.Bootstrap;
using OnVoyage.TestInfrastructure;

namespace Factory.IntegrationTests;

/// <summary>
/// The bootstrap drives the existing pipeline handlers (sources, facts, story, approval, voice, publication) under a cost cap. OpenStreetMap is
/// not needed here: the places are rows of the kind an import and a scoring run leave behind. Providers are the deterministic fakes.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class BootstrapTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private FactoryHarness _f = null!;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(FfmpegAvailable(), "ffmpeg is not installed.");
        _f = await FactoryHarness.StartAsync(postgres);
        await _f.ExecuteAsync("""
            insert into factory.place (id, destination_slug, slug, name, location, qid, osm_type, osm_id, osm_tags, status, annual_pageviews, importance_score, popularity_percentile,
                hidden_gem, crowd_offpeak, crowd_shoulder, crowd_peak, classification_outcome, classification_confidence, editorially_saturated, fragile, access_regulated,
                published_version, source, source_license, source_url, import_run_id, retrieved_at, created_at, updated_at)
            select gen_random_uuid(), 'marseille', 'lieu-' || n, 'Lieu ' || n, st_makepoint(5.36 + n * 0.01, 43.29)::geography, 'QBOOT' || n, 'W', n, '{}', 'Candidate', 1000,
                100 - n * 10, 50, false, 1, 2, 3, 'Rules', 1, false, false, false,
                0, 'osm', 'ODbL-1.0', 'https://www.openstreetmap.org/way/' || n, gen_random_uuid(), now(), now(), now()
            from generate_series(1, 4) as n;
            insert into factory.place_interest (place_id, taxonomy_code, weight, source)
            select id, code, 0.8, 'rule' from factory.place, (values ('history'), ('history.local')) as codes(code);
            insert into factory_raw.wikidata_entity (qid, label_fr, instance_of, heritage_statuses, sitelinks, wikipedia_fr, source, source_license, source_url, retrieved_at)
            select 'QBOOT' || n, 'Lieu ' || n, '{}', '{}', 20, 'Lieu ' || n, 'wikidata', 'CC0-1.0', 'https://www.wikidata.org/wiki/QBOOT' || n, now()
            from generate_series(1, 4) as n;
            """);
    }

    public async ValueTask DisposeAsync()
    {
        if (_f is not null)
        {
            await _f.DisposeAsync();
        }
    }

    private static bool FfmpegAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true });
            process?.WaitForExit(10_000);
            return process?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static BootstrapDestinationCommand Command(double budget = 100, bool autoPublish = true, int places = 4) =>
        new("marseille", MaxPlaces: places, BudgetUsd: budget, AutoPublish: autoPublish, SkipImport: true, PauseMilliseconds: 0, RetryDelaysSeconds: [0.01]);

    private async Task<long> PublishedInCatalogAsync(int pois) =>
        await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select count(*) from catalog.poi") == pois && await _f.CountAsync("select count(*) from catalog.story where status = 'published'") == pois)
            ? pois
            : -1;

    [Fact]
    public async Task The_budget_stops_the_run_cleanly_and_a_second_run_continues_without_redoing_anything()
    {
        _f.Content.Usage.CostPerStory = 1d;

        var first = (await _f.BootstrapAsync(Command(budget: 1.5))).Value.ShouldNotBeNull();

        first.Outcome.ShouldBe(BootstrapDestinationHandler.BudgetExhausted);
        first.Written.ShouldBe(2);
        first.Published.ShouldBe(1, "the second story was written but the cap was reached before its voice: it waits for the next run");
        first.CostUsd.ShouldBe(2d);
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select count(*) from catalog.poi") == 2 && await _f.CountAsync("select count(*) from catalog.story where status = 'published'") == 1))
            .ShouldBeTrue("the two most important places and the first story reach the catalog");
        (await _f.QueryListAsync("select slug from factory.place where status = 'Published' order by slug", r => r.GetString(0))).ShouldBe(["lieu-1", "lieu-2"]);
        (await _f.QueryListAsync("select status from factory.story order by version, place_id", r => r.GetString(0))).Order().ShouldBe(["Approved", "Published"]);
        (await _f.CountAsync("select count(*) from factory.story_audio_part")).ShouldBe(5);
        var voiced = _f.Content.Speech.Requests.Count;
        var written = _f.Content.Writer.Requests.Count;

        var second = (await _f.BootstrapAsync(Command(budget: 10))).Value.ShouldNotBeNull();

        second.Outcome.ShouldBe(BootstrapDestinationHandler.Completed);
        second.Written.ShouldBe(2);
        second.Published.ShouldBe(4, "the story that waited is voiced and published, and the first one already was");
        _f.Content.Writer.Requests.Count.ShouldBe(written + 2, "the first two stories are not written again");
        _f.Content.Speech.Requests.Count.ShouldBe(voiced + 15, "the first story is not voiced again; the second gets its voice now, then the two new ones");
        (await PublishedInCatalogAsync(4)).ShouldBe(4);
        (await _f.CountAsync("select count(*) from factory.story")).ShouldBe(4);

        // A third run has nothing left to do and costs nothing.
        var third = (await _f.BootstrapAsync(Command(budget: 10))).Value.ShouldNotBeNull();
        third.Outcome.ShouldBe(BootstrapDestinationHandler.Completed);
        third.Written.ShouldBe(0);
        third.CostUsd.ShouldBe(0d);
        _f.Content.Writer.Requests.Count.ShouldBe(written + 2);
    }

    [Fact]
    public async Task Without_auto_publish_the_run_stops_at_checked_drafts_and_nothing_reaches_the_catalog()
    {
        var report = (await _f.BootstrapAsync(Command(autoPublish: false, places: 2))).Value.ShouldNotBeNull();

        report.Outcome.ShouldBe(BootstrapDestinationHandler.Completed);
        report.Written.ShouldBe(2);
        report.Published.ShouldBe(0);
        (await _f.QueryListAsync("select status from factory.story order by id", r => r.GetString(0))).ShouldAllBe(status => status == "Checked");
        (await _f.CountAsync("select count(*) from factory.place where status = 'Published'")).ShouldBe(0);
        _f.Content.Speech.Requests.ShouldBeEmpty("no voice before a person approves the text");
        await Task.Delay(500, Ct);
        (await _f.CountAsync("select count(*) from catalog.poi")).ShouldBe(0);
    }

    [Fact]
    public async Task A_paid_provider_without_prices_is_refused_unless_the_caller_accepts_an_uncapped_run()
    {
        _f.Content.Usage.Problem = "A cost cap needs prices.";

        var refused = await _f.BootstrapAsync(Command(places: 1));
        refused.IsSuccess.ShouldBeFalse();
        refused.Error!.Code.ShouldBe("prices_missing");
        _f.Content.Writer.Requests.ShouldBeEmpty();

        var accepted = await _f.BootstrapAsync(Command(places: 1) with { AllowUnpriced = true });
        accepted.IsSuccess.ShouldBeTrue();
        accepted.Value!.Written.ShouldBe(1);
    }

    [Fact]
    public async Task Three_places_in_a_row_with_the_provider_down_stop_the_run_instead_of_burning_retries()
    {
        _f.Content.Extractor.FailFor.UnionWith(["Lieu 1", "Lieu 2", "Lieu 3", "Lieu 4"]);

        var report = (await _f.BootstrapAsync(Command())).Value.ShouldNotBeNull();

        report.Outcome.ShouldBe(BootstrapDestinationHandler.ProviderUnavailable);
        report.Failed.ShouldBe(3);
        report.Written.ShouldBe(0);
        _f.Content.Extractor.CallsByPlace.Keys.Count.ShouldBe(3);
    }

    [Fact]
    public async Task The_admin_api_accepts_a_bootstrap_and_rejects_a_missing_budget()
    {
        var bad = await _f.Admin.PostAsJsonAsync("/api/factory/v1/admin/bootstrap", new { destination = "marseille" }, Ct);
        ((int)bad.StatusCode).ShouldBe(400);

        var ok = await _f.Admin.PostAsJsonAsync("/api/factory/v1/admin/bootstrap", new { destination = "marseille", maxPlaces = 2, budgetUsd = 50, skipImport = true, autoPublish = false }, Ct);
        ((int)ok.StatusCode).ShouldBe(202);
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select count(*) from factory.story where status = 'Checked'") == 2, 90)).ShouldBeTrue("the worker did not run the bootstrap it was sent");
    }
}
