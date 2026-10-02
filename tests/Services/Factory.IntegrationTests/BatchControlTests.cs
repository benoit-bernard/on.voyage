using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnVoyage.Factory.Application.Features.Bootstrap;
using OnVoyage.TestInfrastructure;

namespace Factory.IntegrationTests;

/// <summary>
/// T-404: following, stopping and retrying the mass generation and the bootstrap from the back-office API. The places are rows of the kind an
/// import and a scoring leave behind (no OpenStreetMap needed); providers are the deterministic fakes.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class BatchControlTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Admin = "/api/factory/v1/admin";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private FactoryHarness _f = null!;

    public async ValueTask InitializeAsync()
    {
        _f = await FactoryHarness.StartAsync(postgres);
        await _f.ExecuteAsync("""
            insert into factory.place (id, destination_slug, slug, name, location, qid, osm_type, osm_id, osm_tags, status, annual_pageviews, importance_score, popularity_percentile,
                hidden_gem, crowd_offpeak, crowd_shoulder, crowd_peak, classification_outcome, classification_confidence, editorially_saturated, fragile, access_regulated,
                published_version, source, source_license, source_url, import_run_id, retrieved_at, created_at, updated_at)
            select gen_random_uuid(), 'marseille', 'lieu-' || n, 'Lieu ' || n, st_makepoint(5.36 + n * 0.01, 43.29)::geography, 'QCTRL' || n, 'W', n, '{}', 'Candidate', 1000,
                100 - n * 10, 50, false, 1, 2, 3, 'Rules', 1, false, false, false,
                0, 'osm', 'ODbL-1.0', 'https://www.openstreetmap.org/way/' || n, gen_random_uuid(), now(), now(), now()
            from generate_series(1, 4) as n;
            insert into factory.place_interest (place_id, taxonomy_code, weight, source)
            select id, code, 0.8, 'rule' from factory.place, (values ('history'), ('history.local')) as codes(code);
            insert into factory_raw.wikidata_entity (qid, label_fr, instance_of, heritage_statuses, sitelinks, wikipedia_fr, source, source_license, source_url, retrieved_at)
            select 'QCTRL' || n, 'Lieu ' || n, '{}', '{}', 20, 'Lieu ' || n, 'wikidata', 'CC0-1.0', 'https://www.wikidata.org/wiki/QCTRL' || n, now()
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

    /// <summary>A batch whose jobs are stored in the given state without having been queued: the state the back-office sees mid-run.</summary>
    private async Task<(Guid Batch, List<Guid> Jobs)> InsertBatchAsync(string state, int jobs = 3, double? budget = null, string createdAt = "now() - interval '1 hour'")
    {
        var batch = Guid.CreateVersion7();
        var criteria = JsonSerializer.Serialize(new { destination = "marseille", minImportance = (int?)null, placeStatuses = new[] { "Candidate" }, lang = "fr", kind = "Standard", limit = jobs, budgetUsd = budget });
        await _f.ExecuteAsync($"insert into factory.generation_batch (id, created_at, created_by, criteria, total) values ('{batch}', {createdAt}, 'test', '{criteria}'::jsonb, {jobs})");
        List<Guid> ids = [];
        for (var i = 1; i <= jobs; i++)
        {
            var id = Guid.CreateVersion7();
            ids.Add(id);
            await _f.ExecuteAsync($"""
                insert into factory.generation_job (id, batch_id, place_id, place_name, lang, kind, state, step, attempts, updated_at)
                select '{id}', '{batch}', id, name, 'fr', 'Standard', '{state}', 'queued', 0, now() from factory.place where slug = 'lieu-{i}'
                """);
        }

        return (batch, ids);
    }

    private async Task<JsonElement> DetailAsync(Guid batch) => await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/batches/{batch}", Ct);

    [Fact]
    public async Task Cancelling_stops_the_waiting_jobs_and_a_job_or_the_whole_batch_can_be_put_back_in_the_queue()
    {
        var (batch, jobs) = await InsertBatchAsync("Pending");

        var cancelled = await _f.Admin.PostAsync($"{Admin}/batches/{batch}/cancel", null, Ct);
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cancelled.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("cancelled").GetInt32().ShouldBe(3);
        var detail = await DetailAsync(batch);
        detail.GetProperty("progress").GetProperty("cancelled").GetInt32().ShouldBe(3);
        detail.GetProperty("progress").GetProperty("status").GetString().ShouldBe("cancelled");
        detail.GetProperty("progress").GetProperty("isFinished").GetBoolean().ShouldBeTrue();
        detail.GetProperty("jobs").EnumerateArray().ShouldAllBe(job => job.GetProperty("state").GetString() == "Cancelled" && job.GetProperty("lastError").GetString() == "cancelled_by_admin");
        (await _f.Admin.PostAsync($"{Admin}/batches/{batch}/cancel", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // One job at a time...
        (await _f.Admin.PostAsync($"{Admin}/batch-jobs/{jobs[0]}/retry", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync($"select count(*) from factory.generation_job where id = '{jobs[0]}' and state = 'Succeeded'") == 1, 90)).ShouldBeTrue("the retried job did not finish");
        (await _f.Admin.PostAsync($"{Admin}/batch-jobs/{jobs[0]}/retry", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _f.Admin.PostAsync($"{Admin}/batch-jobs/{Guid.NewGuid()}/retry", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // ...then the rest of the batch.
        var retry = await _f.Admin.PostAsync($"{Admin}/batches/{batch}/retry", null, Ct);
        (await retry.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("requeued").GetInt32().ShouldBe(2);
        (await FactoryHarness.EventuallyAsync(async () => (await DetailAsync(batch)).GetProperty("progress").GetProperty("succeeded").GetInt32() == 3, 90)).ShouldBeTrue("the batch did not finish");
        (await DetailAsync(batch)).GetProperty("progress").GetProperty("status").GetString().ShouldBe("completed");
    }

    [Fact]
    public async Task A_batch_that_reached_its_budget_cancels_the_jobs_that_did_not_start_and_reports_what_it_spent()
    {
        var (batch, _) = await InsertBatchAsync("Failed", budget: 0.5);
        await _f.ExecuteAsync("insert into factory.llm_call (id, kind, model, input_tokens, output_tokens, cost_usd, duration_ms, succeeded, created_at) values (gen_random_uuid(), 'write-story', 'fake', 10, 10, 1.25, 10, true, now() - interval '30 minutes')");
        await _f.ExecuteAsync("insert into factory.llm_call (id, kind, model, input_tokens, output_tokens, cost_usd, duration_ms, succeeded, created_at) values (gen_random_uuid(), 'write-story', 'fake', 10, 10, 9, 10, true, now() - interval '3 hours')");

        (await _f.Admin.PostAsync($"{Admin}/batches/{batch}/retry", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync($"select count(*) from factory.generation_job where batch_id = '{batch}' and state = 'Cancelled'") == 3, 60)).ShouldBeTrue("the jobs were not stopped by the budget");
        var detail = await DetailAsync(batch);
        var progress = detail.GetProperty("progress");
        progress.GetProperty("costUsd").GetDouble().ShouldBe(1.25, 0.0001, "only the calls made since the batch started count");
        progress.GetProperty("batch").GetProperty("criteria").GetProperty("budgetUsd").GetDouble().ShouldBe(0.5);
        detail.GetProperty("jobs").EnumerateArray().ShouldAllBe(job => job.GetProperty("lastError").GetString() == "budget_exhausted");
        (await _f.CountAsync("select count(*) from factory.story")).ShouldBe(0, "nothing was written once the budget was reached");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_batch_budget_must_be_above_zero(double budget)
    {
        var response = await _f.Admin.PostAsJsonAsync($"{Admin}/batches", new { destination = "marseille", limit = 2, budgetUsd = budget }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Jobs_the_queue_gave_up_on_are_listed_as_dead_letters_with_their_place_and_batch()
    {
        _f.Content.Extractor.FailFor.UnionWith(["Lieu 1", "Lieu 2"]);
        var started = await _f.Admin.PostAsJsonAsync($"{Admin}/batches", new { destination = "marseille", lang = "fr", kind = "Standard", limit = 4 }, Ct);
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync(Ct));
        var batch = (await started.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        (await FactoryHarness.EventuallyAsync(async () => (await DetailAsync(batch)).GetProperty("progress").GetProperty("isFinished").GetBoolean(), 90)).ShouldBeTrue("the batch never finished");
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select count(*) from factory.wolverine_dead_letters") == 2, 30)).ShouldBeTrue();

        var letters = (await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/dead-letters", Ct)).EnumerateArray().ToList();
        letters.Count.ShouldBe(2);
        letters.ShouldAllBe(letter => letter.GetProperty("messageType").GetString()!.Contains("RunBatchJobCommand", StringComparison.Ordinal));
        letters.Select(letter => letter.GetProperty("placeName").GetString()).Order().ShouldBe(["Lieu 1", "Lieu 2"]);
        letters.ShouldAllBe(letter => letter.GetProperty("batchId").GetGuid() == batch && letter.GetProperty("exceptionMessage").GetString()!.Contains("failed 3 times", StringComparison.Ordinal));
        (await DetailAsync(batch)).GetProperty("progress").GetProperty("status").GetString().ShouldBe("completed_with_failures");
    }

    [Fact]
    public async Task A_bootstrap_launched_from_the_admin_is_recorded_followed_and_finishes_with_its_counters()
    {
        var accepted = await _f.Admin.PostAsJsonAsync($"{Admin}/bootstrap", new { destination = "marseille", maxPlaces = 2, budgetUsd = 100, skipImport = true }, Ct);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync(Ct));
        var id = (await accepted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        (await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/bootstrap-runs/{id}", Ct)).GetProperty("destination").GetString().ShouldBe("marseille");

        (await FactoryHarness.EventuallyAsync(async () => (await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/bootstrap-runs/{id}", Ct)).GetProperty("isFinished").GetBoolean(), 90)).ShouldBeTrue("the run never finished");

        var run = await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/bootstrap-runs/{id}", Ct);
        run.GetProperty("status").GetString().ShouldBe("Completed");
        run.GetProperty("outcome").GetString().ShouldBe("completed");
        run.GetProperty("placesTotal").GetInt32().ShouldBe(2);
        run.GetProperty("placesDone").GetInt32().ShouldBe(2);
        run.GetProperty("written").GetInt32().ShouldBe(2);
        run.GetProperty("budgetUsd").GetDouble().ShouldBe(100);
        run.GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("step").GetString()).ShouldContain("selection");
        (await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/bootstrap-runs", Ct)).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_bootstrap_stopped_by_an_administrator_ends_as_stopped_without_writing_anything()
    {
        var run = Guid.CreateVersion7();
        await _f.ExecuteAsync($"insert into factory.bootstrap_run (id, destination, status, requested_by, requested_at, max_places, lang, auto_publish, budget_usd, cost_usd, places_total, places_done, written, to_review, published, failed, cancel_requested, steps) values ('{run}', 'marseille', 'Queued', 'test', now(), 2, 'fr', false, 5, 0, 0, 0, 0, 0, 0, 0, false, '[]'::jsonb)");

        var cancel = await _f.Admin.PostAsync($"{Admin}/bootstrap-runs/{run}/cancel", null, Ct);
        cancel.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await _f.BootstrapAsync(new BootstrapDestinationCommand("marseille", MaxPlaces: 2, BudgetUsd: 5, SkipImport: true, PauseMilliseconds: 0, RunId: run));

        result.Value.ShouldNotBeNull().Outcome.ShouldBe(BootstrapDestinationHandler.Cancelled);
        var stored = await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/bootstrap-runs/{run}", Ct);
        stored.GetProperty("status").GetString().ShouldBe("Stopped");
        stored.GetProperty("cancelRequested").GetBoolean().ShouldBeTrue();
        (await _f.CountAsync("select count(*) from factory.story")).ShouldBe(0);
        (await _f.Admin.PostAsync($"{Admin}/bootstrap-runs/{run}/cancel", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _f.Admin.PostAsync($"{Admin}/bootstrap-runs/{Guid.NewGuid()}/cancel", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_bootstrap_started_from_the_command_line_is_recorded_too_and_the_api_refuses_unknown_destinations_and_missing_prices()
    {
        (await _f.BootstrapAsync(new BootstrapDestinationCommand("marseille", MaxPlaces: 1, BudgetUsd: 100, SkipImport: true, PauseMilliseconds: 0))).IsSuccess.ShouldBeTrue();
        (await _f.CountAsync("select count(*) from factory.bootstrap_run where status = 'Completed' and requested_by = 'cli'")).ShouldBe(1);

        (await _f.Admin.PostAsJsonAsync($"{Admin}/bootstrap", new { destination = "atlantis", budgetUsd = 5 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _f.Admin.PostAsJsonAsync($"{Admin}/bootstrap", new { destination = "marseille", budgetUsd = 0 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _f.Admin.PostAsJsonAsync($"{Admin}/bootstrap", new { destination = "marseille", budgetUsd = 5, maxPlaces = 9000 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _f.CountAsync("select count(*) from factory.bootstrap_run")).ShouldBe(1, "a refused request leaves no run behind");
    }

    [Fact]
    public async Task Bootstrap_runs_and_dead_letters_are_for_administrators_only()
    {
        using var traveler = _f.ApiClient(TestTokens.Mint());

        (await traveler.GetAsync($"{Admin}/bootstrap-runs", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await traveler.GetAsync($"{Admin}/dead-letters", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await traveler.PostAsync($"{Admin}/batches/{Guid.NewGuid()}/cancel", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
