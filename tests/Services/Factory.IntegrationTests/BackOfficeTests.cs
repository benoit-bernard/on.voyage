using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnVoyage.TestInfrastructure;

namespace Factory.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class BackOfficeTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Admin = "/api/factory/v1/admin";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private FactoryHarness _f = null!;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(FactoryHarness.Osm2pgsqlAvailable, "osm2pgsql is not installed (apt install osm2pgsql).");
        _f = await FactoryHarness.StartAsync(postgres);
        await _f.RunPipelineAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_f is not null)
        {
            await _f.DisposeAsync();
        }
    }

    private async Task<Guid> IdOfAsync(string slug) => (await _f.GetPlaceAsync(slug)).GetProperty("id").GetGuid();

    private async Task<JsonElement> DetailAsync(Guid id) => await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/places/{id}", Ct);

    [Fact]
    public async Task An_editor_classifies_a_place_no_rule_covers_and_a_new_scoring_keeps_the_choice()
    {
        var mystery = await IdOfAsync("lieu-mysterieux");
        (await _f.GetPlaceAsync("lieu-mysterieux")).GetProperty("status").GetString().ShouldBe("NeedsReview");

        var saved = await _f.Admin.PutAsJsonAsync($"{Admin}/places/{mystery}/interests", new { weights = new Dictionary<string, double> { ["history.military"] = 0.8, ["architecture.defensive"] = 0.6 } }, Ct);

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await DetailAsync(mystery);
        detail.GetProperty("place").GetProperty("status").GetString().ShouldBe("Candidate");
        var weights = detail.GetProperty("interests").EnumerateArray().ToDictionary(i => i.GetProperty("code").GetString()!, i => i.GetProperty("weight").GetDouble());
        weights["history.military"].ShouldBe(0.8, 1e-6);
        weights["history"].ShouldBe(0.8, 1e-6);
        weights["architecture"].ShouldBe(0.6, 1e-6);
        (await _f.CountAsync("select count(*) from factory.place_interest i join factory.place p on p.id = i.place_id where p.slug = 'lieu-mysterieux' and i.source = 'editor'")).ShouldBe(3);

        (await _f.Admin.PostAsJsonAsync($"{Admin}/scorings", new { destination = "marseille" }, Ct)).EnsureSuccessStatusCode();
        await Task.Delay(500, Ct);
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select count(*) from wolverine_queues.wolverine_queue_factory") == 0)).ShouldBeTrue();
        (await _f.CountAsync("select count(*) from factory.place_interest i join factory.place p on p.id = i.place_id where p.slug = 'lieu-mysterieux' and i.source = 'editor'")).ShouldBe(3);
    }

    [Theory]
    [InlineData("history")] // level 1 is derived, not chosen
    [InlineData("nope.nothing")]
    public async Task Unknown_or_level_one_categories_are_refused(string code)
    {
        var mystery = await IdOfAsync("lieu-mysterieux");

        var response = await _f.Admin.PutAsJsonAsync($"{Admin}/places/{mystery}/interests", new { weights = new Dictionary<string, double> { [code] = 0.5 } }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_weight_outside_zero_to_one_is_refused()
    {
        var mystery = await IdOfAsync("lieu-mysterieux");

        (await _f.Admin.PutAsJsonAsync($"{Admin}/places/{mystery}/interests", new { weights = new Dictionary<string, double> { ["history.military"] = 1.5 } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _f.Admin.PutAsJsonAsync($"{Admin}/places/{mystery}/interests", new { weights = new Dictionary<string, double>() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ethics_flags_are_stored_and_travel_to_the_catalog_with_the_next_publication()
    {
        var garde = await IdOfAsync("notre-dame-de-la-garde");

        (await _f.Admin.PutAsJsonAsync($"{Admin}/places/{garde}/ethics", new { fragile = true, accessRegulated = true }, Ct)).EnsureSuccessStatusCode();

        var ethics = (await DetailAsync(garde)).GetProperty("ethics");
        ethics.GetProperty("fragile").GetBoolean().ShouldBeTrue();
        ethics.GetProperty("accessRegulated").GetBoolean().ShouldBeTrue();

        (await _f.Admin.PostAsync($"{Admin}/places/{garde}/publish", null, Ct)).EnsureSuccessStatusCode();
        (await FactoryHarness.EventuallyAsync(async () => await _f.CountAsync("select count(*) from catalog.poi_ethics e join catalog.poi p on p.id = e.poi_id where e.fragile and e.access_regulated") == 1)).ShouldBeTrue("the flags never reached the catalog");
    }

    [Fact]
    public async Task Every_write_is_journaled_with_its_actor_and_outcome_and_reads_are_not()
    {
        var garde = await IdOfAsync("notre-dame-de-la-garde");
        await _f.Admin.GetAsync($"{Admin}/places/{garde}", Ct);
        (await _f.Admin.PutAsJsonAsync($"{Admin}/places/{garde}/editorial", new { importanceOverride = 42, editoriallySaturated = true }, Ct)).EnsureSuccessStatusCode();
        (await _f.Admin.PutAsJsonAsync($"{Admin}/places/{garde}/editorial", new { importanceOverride = 420 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var audit = await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/audit", Ct);

        var entries = audit.EnumerateArray().Where(entry => entry.GetProperty("target").GetString()!.Contains(garde.ToString(), StringComparison.Ordinal)).ToList();
        entries.Count.ShouldBe(2, "only the two writes are journaled");
        entries.Select(entry => entry.GetProperty("status").GetInt32()).Order().ShouldBe([200, 400]);
        entries.ShouldAllBe(entry => entry.GetProperty("action").GetString()!.StartsWith("PUT", StringComparison.Ordinal));
        Guid.TryParse(entries[0].GetProperty("actor").GetString(), out _).ShouldBeTrue();
    }

    [Fact]
    public async Task The_back_office_refuses_anyone_without_the_admin_role()
    {
        using var traveler = _f.ApiClient(TestTokens.Mint());

        (await traveler.GetAsync($"{Admin}/destinations", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await traveler.GetAsync($"{Admin}/audit", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await traveler.PutAsJsonAsync($"{Admin}/pronunciations/marseille/Canebière", new { replacement = "Canebiaire" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var anonymous = _f.ApiClient();
        (await anonymous.GetAsync($"{Admin}/stories", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Destinations_are_listed_from_the_configuration()
    {
        var destinations = await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/destinations", Ct);

        destinations.EnumerateArray().Select(d => d.GetProperty("slug").GetString()).ShouldContain("marseille");
    }

    [Fact]
    public async Task The_pronunciation_lexicon_is_edited_per_destination()
    {
        (await _f.Admin.PutAsJsonAsync($"{Admin}/pronunciations/marseille/Canebière", new { replacement = "Canebiaire" }, Ct)).EnsureSuccessStatusCode();
        (await _f.Admin.PutAsJsonAsync($"{Admin}/pronunciations/marseille/Canebière", new { replacement = "Canébiaire" }, Ct)).EnsureSuccessStatusCode();

        var list = await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/pronunciations?destination=marseille", Ct);
        var entry = list.EnumerateArray().ShouldHaveSingleItem();
        entry.GetProperty("term").GetString().ShouldBe("Canebière");
        entry.GetProperty("replacement").GetString().ShouldBe("Canébiaire");

        (await _f.Admin.PutAsJsonAsync($"{Admin}/pronunciations/atlantis/Terme", new { replacement = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _f.Admin.PutAsJsonAsync($"{Admin}/pronunciations/marseille/Vide", new { replacement = " " }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _f.Admin.DeleteAsync($"{Admin}/pronunciations/marseille/Canebière", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _f.Admin.DeleteAsync($"{Admin}/pronunciations/marseille/Canebière", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
