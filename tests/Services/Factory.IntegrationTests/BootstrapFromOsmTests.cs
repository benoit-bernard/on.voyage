using System.Diagnostics;
using OnVoyage.Factory.Application.Features.Bootstrap;
using OnVoyage.TestInfrastructure;

namespace Factory.IntegrationTests;

/// <summary>The whole chain on the sample OpenStreetMap extract: osm2pgsql, Wikidata (faked), scoring, then stories, voice and publication under a cap.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class BootstrapFromOsmTests(PostgresFixture postgres) : IAsyncLifetime
{
    private FactoryHarness _f = null!;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(FactoryHarness.Osm2pgsqlAvailable, "osm2pgsql is not installed (apt install osm2pgsql).");
        Assert.SkipUnless(FfmpegAvailable(), "ffmpeg is not installed.");
        _f = await FactoryHarness.StartAsync(postgres);
        _f.Wikidata.Entities["Q1457372"] = new OnVoyage.Factory.Application.PlaceEnrichment(
            "Q1457372", "Fort Saint-Jean", null, "Fort", null, ["Q23413"], ["Q10387689"], null, 40, "Fort Saint-Jean (Marseille)", null, null, null, DateTimeOffset.UtcNow);
        _f.Pageviews.ByTitle["Fort Saint-Jean (Marseille)"] = 90_000;
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

    [Fact]
    public async Task From_an_empty_database_to_a_published_place_with_voiced_story_and_a_second_run_that_changes_nothing()
    {
        var command = new BootstrapDestinationCommand("marseille", MaxPlaces: 3, BudgetUsd: 100, AutoPublish: true, PauseMilliseconds: 0, RetryDelaysSeconds: [0.01]);

        var report = (await _f.BootstrapAsync(command)).Value.ShouldNotBeNull();

        report.Outcome.ShouldBe(BootstrapDestinationHandler.Completed);
        report.Steps.Select(step => step.Step).ShouldContain("osm_import");
        report.Steps.Select(step => step.Step).ShouldContain("scoring");
        report.Published.ShouldBe(1, "only the fort has a Wikipedia article; the other places wait for a person");
        report.Steps.Where(step => step.Step.StartsWith("place:", StringComparison.Ordinal)).ShouldAllBe(step => step.Outcome == "skipped");
        (await _f.QueryAsync("select slug from factory.place where status = 'Published'", r => r.GetString(0))).ShouldBe("fort-saint-jean");
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select count(*) from catalog.story where status = 'published' and audio_path is not null") == 1))
            .ShouldBeTrue("the voiced story reaches the catalog");
        (await _f.CountAsync("select count(*) from factory.story_audio_part")).ShouldBe(5);

        var writes = _f.Content.Writer.Requests.Count;
        var voice = _f.Content.Speech.Requests.Count;
        var placeCount = await _f.CountAsync("select count(*) from factory.place");

        var again = (await _f.BootstrapAsync(command)).Value.ShouldNotBeNull();

        again.Steps.Select(step => step.Step).ShouldNotContain("osm_import", "places already known are reused");
        again.Written.ShouldBe(0);
        _f.Content.Writer.Requests.Count.ShouldBe(writes);
        _f.Content.Speech.Requests.Count.ShouldBe(voice);
        (await _f.CountAsync("select count(*) from factory.place")).ShouldBe(placeCount);
    }
}
