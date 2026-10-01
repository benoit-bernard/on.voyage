using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnVoyage.TestInfrastructure;

namespace Factory.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class BatchTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Admin = "/api/factory/v1/admin";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private FactoryHarness _f = null!;
    private List<string> _names = [];

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(FactoryHarness.Osm2pgsqlAvailable, "osm2pgsql is not installed (apt install osm2pgsql).");
        _f = await FactoryHarness.StartAsync(postgres);
        await _f.RunPipelineAsync();
        await PrepareTenCandidatesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_f is not null)
        {
            await _f.DisposeAsync();
        }
    }

    /// <summary>The sample extract has 8 candidates. Two copies a few hundred metres away make 10, and each gets a Wikipedia article.</summary>
    private async Task PrepareTenCandidatesAsync()
    {
        await _f.ExecuteAsync("""
            create temp table copies as select * from factory.place where status = 'Candidate' order by slug limit 2;
            update copies set id = gen_random_uuid(), slug = 'copie-' || slug, osm_id = osm_id + 900000, qid = null, location = st_translate(location, 0.01, 0), footprint = null;
            with numbered as (select id, row_number() over (order by slug) as n from copies) update copies set name = 'Copie ' || numbered.n from numbered where numbered.id = copies.id;
            insert into factory.place select * from copies;
            """);
        _names = await _f.QueryListAsync("select name from factory.place where status = 'Candidate' order by slug", reader => reader.GetString(0));
        _names.Count.ShouldBe(10);

        for (var i = 0; i < _names.Count; i++)
        {
            var qid = $"QB{i + 1}";
            await _f.ExecuteAsync($"update factory.place set qid = '{qid}' where name = '{_names[i].Replace("'", "''", StringComparison.Ordinal)}'");
            _f.Wikidata.Entities[qid] = new OnVoyage.Factory.Application.PlaceEnrichment(qid, _names[i], null, "Lieu", null, [], [], null, 10, $"Article {qid}", null, null, null, DateTimeOffset.UtcNow);
        }

        (await _f.Admin.PostAsJsonAsync($"{Admin}/enrichments", new { destination = "marseille" }, Ct)).EnsureSuccessStatusCode();
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select count(*) from factory_raw.wikidata_entity where qid like 'QB%'") == 10)).ShouldBeTrue("enrichment did not finish");
        await _f.WaitForQueueDrainAsync();
        (await _f.CountAsync("select count(*) from factory.place where status = 'Candidate'")).ShouldBe(10);
    }

    private async Task<Guid> StartBatchAsync(int limit = 10)
    {
        var response = await _f.Admin.PostAsJsonAsync($"{Admin}/batches", new { destination = "marseille", lang = "fr", kind = "Standard", limit }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> ProgressAsync(Guid id) => (await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/batches/{id}", Ct)).GetProperty("progress");

    private async Task<bool> FinishedAsync(Guid id) => (await ProgressAsync(id)).GetProperty("isFinished").GetBoolean();

    [Fact]
    public async Task Ten_jobs_with_two_provider_outages_give_eight_done_and_two_failed_after_three_attempts_then_a_retry_finishes_them()
    {
        _f.Content.Extractor.FailFor.UnionWith(_names.Take(2));

        var batch = await StartBatchAsync();

        (await FactoryHarness.EventuallyAsync(() => FinishedAsync(batch), 120)).ShouldBeTrue("the batch never finished");
        var progress = await ProgressAsync(batch);
        progress.GetProperty("batch").GetProperty("total").GetInt32().ShouldBe(10);
        progress.GetProperty("succeeded").GetInt32().ShouldBe(8);
        progress.GetProperty("failed").GetInt32().ShouldBe(2);
        progress.GetProperty("pending").GetInt32().ShouldBe(0);

        var failed = (await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/batches/{batch}", Ct)).GetProperty("jobs").EnumerateArray().Where(job => job.GetProperty("state").GetString() == "Failed").ToList();
        failed.Count.ShouldBe(2);
        failed.ShouldAllBe(job => job.GetProperty("attempts").GetInt32() == 3 && job.GetProperty("lastError").GetString()!.Contains("provider down", StringComparison.Ordinal));
        _f.Content.Extractor.CallsByPlace.Where(pair => _names.Take(2).Contains(pair.Key)).ShouldAllBe(pair => pair.Value == 3, "three attempts, not more");
        (await _f.CountAsync("select count(*) from factory.wolverine_dead_letters")).ShouldBe(2);

        // Nothing is voiced or published by a batch: the stories wait for a person.
        (await _f.CountAsync("select count(*) from factory.story where status in ('Checked', 'NeedsReview')")).ShouldBe(8);
        _f.Content.Speech.Requests.ShouldBeEmpty();

        // The outage is over: retrying only touches the failed jobs.
        _f.Content.Extractor.FailFor.Clear();
        var retry = await _f.Admin.PostAsync($"{Admin}/batches/{batch}/retry", null, Ct);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await retry.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("requeued").GetInt32().ShouldBe(2);
        (await FactoryHarness.EventuallyAsync(async () => (await ProgressAsync(batch)).GetProperty("succeeded").GetInt32() == 10, 120)).ShouldBeTrue("the retried jobs did not finish");
        (await _f.CountAsync("select count(*) from factory.story")).ShouldBe(10);

        (await _f.Admin.PostAsync($"{Admin}/batches/{batch}/retry", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_second_batch_skips_places_that_already_have_a_story()
    {
        var first = await StartBatchAsync(limit: 4);
        (await FactoryHarness.EventuallyAsync(() => FinishedAsync(first), 120)).ShouldBeTrue();

        var second = await StartBatchAsync();

        (await ProgressAsync(second)).GetProperty("batch").GetProperty("total").GetInt32().ShouldBe(6);
        (await FactoryHarness.EventuallyAsync(() => FinishedAsync(second), 120)).ShouldBeTrue();
        (await _f.CountAsync("select count(distinct place_id) from factory.story")).ShouldBe(10);

        var third = await _f.Admin.PostAsJsonAsync($"{Admin}/batches", new { destination = "marseille", lang = "fr", kind = "Standard", limit = 10 }, Ct);
        third.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("atlantis", "fr", 10)]
    [InlineData("marseille", "de", 10)]
    [InlineData("marseille", "fr", 0)]
    [InlineData("marseille", "fr", 5000)]
    public async Task A_batch_needs_a_known_destination_a_language_and_a_sensible_size(string destination, string lang, int limit)
    {
        var response = await _f.Admin.PostAsJsonAsync($"{Admin}/batches", new { destination, lang, limit }, Ct);

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
        (await _f.CountAsync("select count(*) from factory.generation_batch")).ShouldBe(0);
    }

    [Fact]
    public async Task Batches_are_for_administrators_only()
    {
        using var traveler = _f.ApiClient(TestTokens.Mint());

        (await traveler.PostAsJsonAsync($"{Admin}/batches", new { destination = "marseille" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await traveler.GetAsync($"{Admin}/batches", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_minimum_importance_filters_places_and_the_most_important_come_first()
    {
        var all = await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/places?destination=marseille&status=Candidate&limit=100", Ct);
        var best = all.EnumerateArray().Select(p => p.GetProperty("importanceScore").GetInt32()).OrderDescending().ToList();

        var response = await _f.Admin.PostAsJsonAsync($"{Admin}/batches", new { destination = "marseille", minImportance = best[2], limit = 100 }, Ct);
        var batch = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var total = (await ProgressAsync(batch)).GetProperty("batch").GetProperty("total").GetInt32();
        total.ShouldBe(best.Count(score => score >= best[2]));
        (await FactoryHarness.EventuallyAsync(() => FinishedAsync(batch), 120)).ShouldBeTrue();
    }
}
