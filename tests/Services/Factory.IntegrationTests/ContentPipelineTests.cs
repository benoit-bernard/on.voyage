using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnVoyage.TestInfrastructure;

namespace Factory.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class ContentPipelineTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Admin = "/api/factory/v1/admin";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private FactoryHarness _f = null!;
    private Guid _place;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(FactoryHarness.Osm2pgsqlAvailable, "osm2pgsql is not installed (apt install osm2pgsql).");
        Assert.SkipUnless(FfmpegAvailable(), "ffmpeg is not installed.");
        _f = await FactoryHarness.StartAsync(postgres);
        _f.Wikidata.Entities["Q1457372"] = new OnVoyage.Factory.Application.PlaceEnrichment(
            "Q1457372", "Fort Saint-Jean", null, "Fort", null, ["Q23413"], ["Q10387689"], null, 40, "Fort Saint-Jean (Marseille)", null, null, null, DateTimeOffset.UtcNow);
        _f.Pageviews.ByTitle["Fort Saint-Jean (Marseille)"] = 90_000;
        await _f.RunPipelineAsync();
        _place = (await _f.GetPlaceAsync("fort-saint-jean")).GetProperty("id").GetGuid();
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

    private async Task<JsonElement> PostAsync(string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await _f.Admin.PostAsJsonAsync(url, body ?? new { }, Ct);
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));
        return expected == HttpStatusCode.Accepted ? default : await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<Guid> WriteStoryAsync()
    {
        await PostAsync($"{Admin}/places/{_place}/sources", expected: HttpStatusCode.Accepted);
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select count(*) from factory.fact where status = 'Validated'") == 3)).ShouldBeTrue("facts were not extracted");
        await PostAsync($"{Admin}/places/{_place}/stories", new { lang = "fr", kind = "Standard" }, HttpStatusCode.Accepted);
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select count(*) from factory.story where status in ('Checked', 'NeedsReview')") == 1)).ShouldBeTrue("the story was not written");
        return await _f.QueryAsync("select id from factory.story", r => r.GetGuid(0));
    }

    [Fact]
    public async Task Facts_come_from_quotes_found_word_for_word_and_the_writer_never_sees_the_source_text()
    {
        var story = await WriteStoryAsync();

        // The invented claim ("trésor caché") has no matching quote in the article: it is rejected, not silently kept.
        (await _f.CountAsync("select count(*) from factory.fact where status = 'Rejected' and reason = 'quote_not_found'")).ShouldBe(1);
        (await _f.QueryAsync("select status from factory.story", r => r.GetString(0))).ShouldBe("Checked");

        var request = _f.Content.Writer.Requests.ShouldHaveSingleItem();
        request.Facts.Count.ShouldBe(3);
        request.Facts.ShouldAllBe(fact => !FakeContentServices.Article.Contains(fact.Statement, StringComparison.Ordinal));
        typeof(OnVoyage.Factory.Application.Content.StoryWriteRequest).GetProperties().Select(p => p.Name).ShouldNotContain("DocumentText");

        var detail = await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/stories/{story}", Ct);
        detail.GetProperty("story").GetProperty("report").GetProperty("overlap").GetProperty("passes").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Audio_is_only_made_after_approval_then_the_published_story_reaches_the_catalog_with_its_sources()
    {
        var story = await WriteStoryAsync();

        // Nothing is voiced, and nothing is published, before a person approved the text and listened to the audio.
        (await _f.Admin.PostAsync($"{Admin}/stories/{story}/publish", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await PostAsync($"{Admin}/stories/{story}/audio", expected: HttpStatusCode.Accepted);
        await InboxDrainedAsync();
        _f.Content.Speech.Requests.ShouldBeEmpty();

        await PostAsync($"{Admin}/stories/{story}/approve", new { editorialScore = 0.9 });
        await PostAsync($"{Admin}/stories/{story}/audio", expected: HttpStatusCode.Accepted);
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select count(*) from factory.story where status = 'AudioReady'") == 1)).ShouldBeTrue("audio was not generated");
        _f.Content.Speech.Requests.Count.ShouldBe(5, string.Join(" | ", _f.Content.Speech.Requests.Select(r => r.Text[..Math.Min(25, r.Text.Length)])));

        // The same job again does not pay for the same speech twice.
        await PostAsync($"{Admin}/stories/{story}/audio", expected: HttpStatusCode.Accepted);
        await InboxDrainedAsync();
        _f.Content.Speech.Requests.Count.ShouldBe(5, string.Join(" | ", _f.Content.Speech.Requests.Select(r => r.Text[..Math.Min(25, r.Text.Length)])));

        var path = await _f.QueryAsync("select path from factory.story_audio_part where part = 'main'", r => r.GetString(0));
        var file = Path.Combine(_f.MediaDirectory, path);
        File.Exists(file).ShouldBeTrue();
        var probe = await ProbeAsync(file);
        probe.GetProperty("streams")[0].GetProperty("channels").GetInt32().ShouldBe(1);
        probe.GetProperty("streams")[0].GetProperty("sample_rate").GetString().ShouldBe("44100");
        var tags = probe.GetProperty("format").GetProperty("tags");
        tags.EnumerateObject().Any(tag => tag.Name.Equals("AI_GENERATED", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue("the file must say it is AI-generated");

        // Publishing needs the place to be in the catalog first.
        (await _f.Admin.PostAsync($"{Admin}/stories/{story}/publish", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _f.Admin.PostAsync($"{Admin}/places/{_place}/publish", null, Ct)).EnsureSuccessStatusCode();
        await PostAsync($"{Admin}/stories/{story}/publish");

        (await FactoryHarness.EventuallyAsync(async () =>
        {
            var response = await _f.Traveler.GetAsync("/api/catalog/v1/pois/fort-saint-jean", Ct);
            return response.IsSuccessStatusCode && (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("stories").GetArrayLength() == 1;
        })).ShouldBeTrue("the story never reached the catalog");

        var poi = await _f.Traveler.GetFromJsonAsync<JsonElement>("/api/catalog/v1/pois/fort-saint-jean", Ct);
        var projected = poi.GetProperty("stories")[0];
        projected.GetProperty("aiGenerated").GetBoolean().ShouldBeTrue();
        projected.GetProperty("audioUrl").GetString().ShouldEndWith(path);
        poi.GetProperty("attributions").EnumerateArray().Select(a => a.GetString()).ShouldContain(a => a!.Contains("CC BY-SA", StringComparison.Ordinal) && a.Contains("Fort Saint-Jean", StringComparison.Ordinal));

        // And the audio is served from the catalog, anonymously through the media path.
        (await _f.Traveler.GetAsync(projected.GetProperty("audioUrl").GetString(), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Suspending takes it off the catalog again.
        await PostAsync($"{Admin}/stories/{story}/suspend", new { reason = "Erreur signalée" });
        (await FactoryHarness.EventuallyAsync(async () =>
            (await _f.Traveler.GetFromJsonAsync<JsonElement>("/api/catalog/v1/pois/fort-saint-jean", Ct)).GetProperty("stories").GetArrayLength() == 0)).ShouldBeTrue("the suspended story stayed visible");
    }

    [Fact]
    public async Task Three_distinct_travelers_reporting_a_story_suspend_it()
    {
        var story = await WriteStoryAsync();
        await PostAsync($"{Admin}/stories/{story}/approve", new { editorialScore = 0.9 });
        await PostAsync($"{Admin}/stories/{story}/audio", expected: HttpStatusCode.Accepted);
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select count(*) from factory.story where status = 'AudioReady'") == 1)).ShouldBeTrue();
        (await _f.Admin.PostAsync($"{Admin}/places/{_place}/publish", null, Ct)).EnsureSuccessStatusCode();
        await PostAsync($"{Admin}/stories/{story}/publish");

        for (var i = 0; i < 3; i++)
        {
            using var traveler = _f.ApiClient(TestTokens.Mint(Guid.NewGuid()));
            var response = await traveler.PostAsJsonAsync($"/api/factory/v1/stories/{story}/reports", new { reason = "Une date semble fausse" }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        }

        (await _f.QueryAsync("select status from factory.story", r => r.GetString(0))).ShouldBe("Suspended");

        // A traveler token cannot reach the back office.
        using var plain = _f.ApiClient(TestTokens.Mint());
        (await plain.GetAsync($"{Admin}/stories/{story}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>Waits until the worker has handled every queued job, so "nothing happened" is a fact and not a race.</summary>
    private async Task InboxDrainedAsync() =>
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select (select count(*) from wolverine_queues.wolverine_queue_factory) + (select count(*) from factory.wolverine_incoming_envelopes where status = 'Incoming')") == 0)).ShouldBeTrue("the worker did not drain its queue");

    private async Task<Guid> PublishedStoryAsync()
    {
        var story = await WriteStoryAsync();
        await PostAsync($"{Admin}/stories/{story}/approve", new { editorialScore = 0.9 });
        await PostAsync($"{Admin}/stories/{story}/audio", expected: HttpStatusCode.Accepted);
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select count(*) from factory.story where status = 'AudioReady'") == 1)).ShouldBeTrue();
        (await _f.Admin.PostAsync($"{Admin}/places/{_place}/publish", null, Ct)).EnsureSuccessStatusCode();
        await PostAsync($"{Admin}/stories/{story}/publish");
        return story;
    }

    private async Task ReportAsync(Guid story, int travelers)
    {
        for (var i = 0; i < travelers; i++)
        {
            using var traveler = _f.ApiClient(TestTokens.Mint(Guid.NewGuid()));
            (await traveler.PostAsJsonAsync($"/api/factory/v1/stories/{story}/reports", new { reason = $"Erreur {i}" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    private async Task<JsonElement> InboxAsync(string status) => await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/reports?status={status}", Ct);

    [Fact]
    public async Task Reported_stories_land_in_the_inbox_without_reader_identities_and_opening_a_correction_closes_them()
    {
        var story = await PublishedStoryAsync();
        await ReportAsync(story, 3);
        (await _f.QueryAsync("select status from factory.story", r => r.GetString(0))).ShouldBe("Suspended");

        var open = await InboxAsync("Open");
        var item = open.EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("storyId").GetGuid().ShouldBe(story);
        item.GetProperty("placeName").GetString().ShouldBe("Fort Saint-Jean");
        item.GetProperty("openReports").GetInt32().ShouldBe(3);
        item.GetProperty("remarks").GetArrayLength().ShouldBe(3);
        open.GetRawText().ShouldNotContain("travelerId", Case.Insensitive);

        var correction = await PostAsync($"{Admin}/stories/{story}/correction");
        (await InboxAsync("Open")).GetArrayLength().ShouldBe(0);
        var handled = (await InboxAsync("Handled")).EnumerateArray().ShouldHaveSingleItem();
        handled.GetProperty("remarks").EnumerateArray().ShouldAllBe(remark => remark.GetProperty("resolution").GetString() == $"correction v{correction.GetProperty("version").GetInt32()}");
        (await _f.Admin.PostAsJsonAsync($"{Admin}/stories/{story}/reports/resolve", new { status = "Handled" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private async Task ReportKindAsync(Guid story, string reason, int travelers = 1)
    {
        for (var i = 0; i < travelers; i++)
        {
            using var traveler = _f.ApiClient(TestTokens.Mint(Guid.NewGuid()));
            (await traveler.PostAsJsonAsync($"/api/factory/v1/stories/{story}/reports", new { reason }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Only_inaccurate_fact_reports_suspend_a_story_and_the_inbox_can_be_filtered_by_kind()
    {
        var story = await PublishedStoryAsync();

        await ReportKindAsync(story, "[Pronunciation] On dit Marseille avec le s final.", 4);
        await ReportKindAsync(story, "[Photo] La photo montre un autre fort.");
        (await _f.QueryAsync("select status from factory.story", r => r.GetString(0))).ShouldBe("Published", "five readers, but none of them reported a wrong fact");

        await ReportKindAsync(story, "[InaccurateFact] La date est fausse.", 2);
        (await _f.QueryAsync("select status from factory.story", r => r.GetString(0))).ShouldBe("Published", "two inaccurate facts are not enough");
        await ReportKindAsync(story, "Une date semble fausse (rapport sans type, application ancienne)");
        (await _f.QueryAsync("select status from factory.story", r => r.GetString(0))).ShouldBe("Suspended");

        var item = (await InboxAsync("Open")).EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("openReports").GetInt32().ShouldBe(8);
        var kinds = item.GetProperty("remarks").EnumerateArray().Select(remark => remark.GetProperty("kind").GetString()).GroupBy(kind => kind).ToDictionary(group => group.Key!, group => group.Count());
        kinds.ShouldBe(new Dictionary<string, int> { ["Pronunciation"] = 4, ["Photo"] = 1, ["InaccurateFact"] = 3 });
        item.GetProperty("remarks").EnumerateArray().Select(remark => remark.GetProperty("reason").GetString()).ShouldNotContain(reason => reason!.StartsWith('['), "the kind is shown apart from the text");

        (await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/reports?status=Open&kind=Pronunciation", Ct)).GetArrayLength().ShouldBe(1);
        (await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/reports?status=Open&kind=ClosedOrMoved", Ct)).GetArrayLength().ShouldBe(0);
        (await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/reports?status=Open&kind=InaccurateFact", Ct)).GetArrayLength().ShouldBe(1);
        (await _f.Admin.GetAsync($"{Admin}/reports?kind=Nope", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Handling_the_reports_of_a_story_is_journaled_with_the_editor_and_the_decision()
    {
        var story = await PublishedStoryAsync();
        await ReportKindAsync(story, "[ClosedOrMoved] Le fort est fermé le lundi.");

        await PostAsync($"{Admin}/stories/{story}/reports/resolve", new { status = "Handled", note = "Horaires ajoutes" });

        (await _f.CountAsync($"select count(*) from factory.audit_log where action like 'POST%' and target = '{Admin}/stories/{story}/reports/resolve' and status = 200 and detail like '%Horaires ajoutes%'")).ShouldBe(1);
        (await _f.CountAsync("select count(*) from factory.audit_log where actor <> 'unknown'")).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Putting_a_story_back_online_dismisses_its_reports_and_only_new_reports_count_toward_the_threshold()
    {
        var story = await PublishedStoryAsync();
        await ReportAsync(story, 3);

        await PostAsync($"{Admin}/stories/{story}/resume");

        (await InboxAsync("Open")).GetArrayLength().ShouldBe(0);
        (await InboxAsync("Dismissed")).EnumerateArray().ShouldHaveSingleItem().GetProperty("remarks").GetArrayLength().ShouldBe(3);
        await ReportAsync(story, 1);
        (await _f.QueryAsync("select status from factory.story", r => r.GetString(0))).ShouldBe("Published");
        (await InboxAsync("Open")).EnumerateArray().ShouldHaveSingleItem().GetProperty("openReports").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task An_editor_can_dismiss_the_reports_of_a_story_with_a_note_and_bad_input_is_refused()
    {
        var story = await PublishedStoryAsync();
        await ReportAsync(story, 1);

        (await _f.Admin.PostAsJsonAsync($"{Admin}/stories/{story}/reports/resolve", new { status = "Open" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _f.Admin.PostAsJsonAsync($"{Admin}/stories/{story}/reports/resolve", new { status = "Dismissed", note = new string('x', 301) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _f.Admin.GetAsync($"{Admin}/reports?status=Whatever", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _f.Admin.PostAsJsonAsync($"{Admin}/stories/{Guid.NewGuid()}/reports/resolve", new { status = "Dismissed" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var closed = await PostAsync($"{Admin}/stories/{story}/reports/resolve", new { status = "Dismissed", note = "Source officielle vérifiée" });
        closed.GetProperty("closed").GetInt32().ShouldBe(1);
        var remark = (await InboxAsync("Dismissed")).EnumerateArray().ShouldHaveSingleItem().GetProperty("remarks")[0];
        remark.GetProperty("resolution").GetString().ShouldBe("Source officielle vérifiée");
        (await _f.QueryAsync("select status from factory.story", r => r.GetString(0))).ShouldBe("Published");
    }

    private static async Task<JsonElement> ProbeAsync(string file)
    {
        using var process = Process.Start(new ProcessStartInfo("ffprobe", $"-v quiet -print_format json -show_format -show_streams \"{file}\"") { RedirectStandardOutput = true })!;
        var output = await process.StandardOutput.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return JsonDocument.Parse(output).RootElement.Clone();
    }
}
