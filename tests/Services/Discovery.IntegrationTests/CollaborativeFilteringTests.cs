using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OnVoyage.Discovery.Api;
using OnVoyage.Discovery.Application.IntegrationEvents;
using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Factory.Contracts;
using OnVoyage.Taxonomy;
using OnVoyage.TestInfrastructure;

namespace Discovery.IntegrationTests;

/// <summary>
/// T-504 on PostgreSQL: the job computes the neighbours' scores into <c>discovery.cf_score</c>, the API reads them (<c>/me/cf-scores</c>, the ranking),
/// and the privacy guards hold (few neighbours, small population, control cohort, deleted traveler). Travelers are written straight to the tables:
/// what matters here is the computation, not the learning rule that produced the vectors.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CollaborativeFilteringTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Recompute = "/api/discovery/v1/admin/cf-scores/recompute";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private string _connection = string.Empty;
    private WebApplicationFactory<DiscoveryApiMarker> _factory = null!;
    private readonly List<Guid> _places = [];

    public async ValueTask InitializeAsync()
    {
        _connection = await postgres.CreateDatabaseAsync();
        _factory = Host(_connection);
        _ = _factory.Server;
        await SeedPlacesAsync();
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private static WebApplicationFactory<DiscoveryApiMarker> Host(string connection, string? startupDelay = null) =>
        new WebApplicationFactory<DiscoveryApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", connection);
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
            builder.UseSetting("Media:PublicBaseUrl", "https://media.test/media");

            // The job is off unless a test is about the job: the endpoint runs the same command.
            builder.UseSetting("Discovery:Cf:Enabled", startupDelay is null ? "false" : "true");
            builder.UseSetting("Discovery:Cf:StartupDelayMinutes", startupDelay ?? "120");
        });

    private async Task SeedPlacesAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IProjectionWriter>();
        for (var i = 0; i < 6; i++)
        {
            var id = Guid.NewGuid();
            _places.Add(id);
            await PoiPublishedHandler.Handle(
                new PoiPublishedV1(
                    Guid.NewGuid(), DateTimeOffset.UtcNow, id, 1, new PoiDestinationV1("marseille", "Marseille", 43.2965, 5.3698), $"lieu-{i}", $"Lieu {i}", null,
                    43.2965 + (i * 0.002), 5.3698, 50, 50, false, 0.7f, 1,
                    [new PoiInterestV1("history", 0.9f), new PoiInterestV1("history.military", 1f)], new PoiCrowdProfileV1(1, 2, 2), false, false),
                writer, Ct);
            await StoryPublishedHandler.Handle(
                new StoryPublishedV1(
                    Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), id, "fr", "standard", 1, $"Lieu {i}", "", "", "", 100, "v", true, false,
                    [new StoryAudioPartV1("main", $"s/{i}.mp3", "x", 100)], []),
                writer, Ct);
        }
    }

    private static float[] Taste(string category)
    {
        var vector = new float[Interests.All.Count];
        vector[Interests.All.ToList().IndexOf(category)] = 0.9f;
        return vector;
    }

    private async Task SeedTravelerAsync(Guid id, string category, int depth, string cohort = "personalized", params (Guid Poi, double Rating)[] ratings)
    {
        await using var connection = new NpgsqlConnection(_connection);
        await connection.OpenAsync(Ct);
        await Execute(connection, "insert into discovery.traveler (id, lang, ethical_mode, is_premium, profile_depth, cohort, last_active_at, created_at) values (@id, 'fr', 'balanced', false, @depth, @cohort, now(), now())",
            ("id", id), ("depth", depth), ("cohort", cohort));
        await Execute(connection, "insert into discovery.interest_vector (traveler_id, vector, taxonomy_version, locks, updated_at) values (@id, @vector, 1, '{}'::jsonb, now())", ("id", id), ("vector", Taste(category)));
        foreach (var (poi, rating) in ratings)
        {
            await Execute(connection, "insert into discovery.poi_rating (traveler_id, poi_id, rating, excluded, updated_at) values (@id, @poi, @rating, false, now())", ("id", id), ("poi", poi), ("rating", rating));
        }
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connection);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connection);
        await connection.OpenAsync(Ct);
        await Execute(connection, sql);
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private HttpClient As(Guid id, bool admin = false)
    {
        var client = _factory.CreateClient();
        client.Authenticate(TestTokens.Mint(id, roles: admin ? ["admin"] : null));
        return client;
    }

    /// <summary>22 history fans who loved place 0, liked place 1 a bit, and two of whom rated place 2; three nature fans who disliked place 0; and the traveler "me".</summary>
    private async Task<Guid> SeedCommunityAsync()
    {
        for (var i = 0; i < 22; i++)
        {
            var ratings = new List<(Guid, double)> { (_places[0], 1d), (_places[1], 0.4d) };
            if (i < 2)
            {
                ratings.Add((_places[2], 1d));
            }

            await SeedTravelerAsync(Guid.NewGuid(), "history", depth: 12, ratings: [.. ratings]);
        }

        for (var i = 0; i < 3; i++)
        {
            await SeedTravelerAsync(Guid.NewGuid(), "nature", depth: 12, ratings: [(_places[0], -1d)]);
        }

        var me = Guid.NewGuid();
        await SeedTravelerAsync(me, "history", depth: 12);
        return me;
    }

    private async Task<JsonElement> RunJobAsync()
    {
        using var admin = As(Guid.NewGuid(), admin: true);
        var response = await admin.PostAsync(Recompute, null, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    [Fact]
    public async Task The_job_stores_the_neighbours_scores_and_the_endpoint_serves_them_without_naming_anyone()
    {
        var me = await SeedCommunityAsync();

        var summary = await RunJobAsync();

        summary.GetProperty("outcome").GetString().ShouldBe("computed");
        summary.GetProperty("pool").GetInt32().ShouldBe(26);
        summary.GetProperty("travelers").GetInt32().ShouldBe(26);
        (await CountAsync($"select count(*) from discovery.cf_score where traveler_id = '{me}' and poi_id = '{_places[0]}' and support = 22 and score > 0.7")).ShouldBe(1);
        (await CountAsync($"select count(*) from discovery.cf_score where traveler_id = '{me}' and poi_id = '{_places[1]}' and score > 0 and score < 0.4")).ShouldBe(1);
        (await CountAsync($"select count(*) from discovery.cf_score where poi_id = '{_places[2]}'")).ShouldBe(0, "two neighbours rated it: below the minimum of three, so no score");

        using var client = As(me);
        var response = await client.GetAsync("/api/discovery/v1/me/cf-scores?destination=marseille", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        var scores = JsonSerializer.Deserialize<CfScoresDto>(body, Web)!;
        scores.Items.Count.ShouldBe(6);
        var loved = scores.Items.Single(item => item.PoiId == _places[0]);
        loved.Cf.ShouldNotBeNull().ShouldBeGreaterThan(0.7);
        loved.Support.ShouldBe(22);
        scores.Items.Single(item => item.PoiId == _places[2]).Cf.ShouldBeNull();
        scores.Items.Single(item => item.PoiId == _places[2]).Support.ShouldBeNull();
        scores.Items.Single(item => item.PoiId == _places[5]).Cf.ShouldBeNull();

        // Nothing identifies a neighbour: only places, scores and counts.
        var others = await CountAsync("select count(*) from discovery.traveler");
        others.ShouldBe(26);
        body.ShouldNotContain("travelerId", Case.Insensitive);
    }

    [Fact]
    public async Task A_place_the_neighbours_loved_ranks_higher_for_the_traveler_than_without_their_scores()
    {
        var me = await SeedCommunityAsync();
        using var client = As(me);
        async Task<RecommendationItemDto> LovedAsync() =>
            (await client.GetFromJsonAsync<RecommendationsDto>("/api/discovery/v1/recommendations?limit=6&radius=20000", Ct))!.Items.Single(item => item.PoiId == _places[0]);

        var before = await LovedAsync();
        await RunJobAsync();
        var after = await LovedAsync();
        var dislikedBeforeRun = (await client.GetFromJsonAsync<RecommendationsDto>("/api/discovery/v1/recommendations?limit=6&radius=20000", Ct))!.Items.Single(item => item.PoiId == _places[1]);

        after.Score.ShouldBeGreaterThan(before.Score, "twenty-two travelers with the same taste loved it");
        dislikedBeforeRun.Score.ShouldBeLessThan(after.Score);
        (await client.GetFromJsonAsync<RecommendationsDto>("/api/discovery/v1/recommendations?limit=6&radius=20000", Ct))!.WeightsVersion.ShouldBe(1);
    }

    [Fact]
    public async Task The_scores_of_a_place_with_few_neighbours_weigh_proportionally_less()
    {
        var me = await SeedCommunityAsync();
        await RunJobAsync();
        using var client = As(me);
        async Task<double> ScoreOf(Guid place) =>
            (await client.GetFromJsonAsync<RecommendationsDto>("/api/discovery/v1/recommendations?limit=6&radius=20000", Ct))!.Items.Single(item => item.PoiId == place).Score;
        var full = await ScoreOf(_places[0]);

        // Same score, but only 5 neighbours stand behind it: half the weight.
        await ExecuteAsync($"update discovery.cf_score set support = 5 where traveler_id = '{me}' and poi_id = '{_places[0]}'");
        var half = await ScoreOf(_places[0]);
        await ExecuteAsync($"delete from discovery.cf_score where traveler_id = '{me}'");
        var none = await ScoreOf(_places[0]);

        full.ShouldBeGreaterThan(half);
        half.ShouldBeGreaterThan(none);
        (full - half).ShouldBe(half - none, 0.002, "the weight is proportional to the support up to 10 neighbours");
    }

    [Fact]
    public async Task The_control_cohort_and_travelers_in_cold_start_get_no_collaborative_score()
    {
        var me = await SeedCommunityAsync();
        var control = Guid.NewGuid();
        await SeedTravelerAsync(control, "history", depth: 12, cohort: "control");
        var newcomer = Guid.NewGuid();
        await SeedTravelerAsync(newcomer, "history", depth: 2);

        await RunJobAsync();

        (await CountAsync($"select count(*) from discovery.cf_score where traveler_id = '{newcomer}'")).ShouldBe(0);
        using var controlClient = As(control);
        var forControl = (await controlClient.GetFromJsonAsync<CfScoresDto>("/api/discovery/v1/me/cf-scores?destination=marseille", Ct))!;
        forControl.Cohort.ShouldBe("control");
        forControl.Items.ShouldAllBe(item => item.Cf == null && item.Support == null, "the control cohort ranks by importance and distance only (F-03)");
        using var client = As(me);
        (await client.GetFromJsonAsync<CfScoresDto>("/api/discovery/v1/me/cf-scores?destination=marseille", Ct))!.Items.ShouldContain(item => item.Cf != null);
    }

    [Fact]
    public async Task With_too_few_travelers_no_score_is_produced_and_an_earlier_one_is_removed()
    {
        var me = await SeedCommunityAsync();
        await RunJobAsync();
        (await CountAsync("select count(*) from discovery.cf_score")).ShouldBeGreaterThan(0);

        // Some travelers leave (or stop being active): the population falls under 20, nobody can hide in it any more.
        await ExecuteAsync($"delete from discovery.traveler where id in (select id from discovery.traveler where id <> '{me}' order by id limit 10)");
        var summary = await RunJobAsync();

        summary.GetProperty("outcome").GetString().ShouldBe("pool_too_small");
        summary.GetProperty("pool").GetInt32().ShouldBe(16);
        (await CountAsync("select count(*) from discovery.cf_score")).ShouldBe(0);
    }

    [Fact]
    public async Task Running_the_job_again_gives_the_same_rows_and_a_traveler_who_goes_inactive_loses_hers()
    {
        var me = await SeedCommunityAsync();
        await RunJobAsync();
        var rows = await CountAsync("select count(*) from discovery.cf_score");
        var checksum = await CountAsync("select coalesce(sum(round((score * 100000)::numeric)), 0)::bigint from discovery.cf_score");

        await RunJobAsync();

        (await CountAsync("select count(*) from discovery.cf_score")).ShouldBe(rows);
        (await CountAsync("select coalesce(sum(round((score * 100000)::numeric)), 0)::bigint from discovery.cf_score")).ShouldBe(checksum);

        await ExecuteAsync($"update discovery.traveler set last_active_at = now() - interval '400 days' where id = '{me}'");
        var summary = await RunJobAsync();

        summary.GetProperty("removed").GetInt32().ShouldBeGreaterThan(0);
        (await CountAsync($"select count(*) from discovery.cf_score where traveler_id = '{me}'")).ShouldBe(0, "a traveler not seen for more than a year is not computed for");
    }

    [Fact]
    public async Task Deleting_a_traveler_deletes_her_scores_and_the_export_lists_them()
    {
        var me = await SeedCommunityAsync();
        await RunJobAsync();
        (await CountAsync($"select count(*) from discovery.cf_score where traveler_id = '{me}'")).ShouldBeGreaterThan(0);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var rights = scope.ServiceProvider.GetRequiredService<OnVoyage.Discovery.Application.Features.IDataRightsStore>();
            var export = JsonDocument.Parse(await rights.ExportAsync(me, Ct)).RootElement;
            export.GetProperty("collaborativeScores").GetArrayLength().ShouldBeGreaterThan(0);
            (await rights.DeleteTravelerAsync(me, Ct)).ShouldBeTrue();
        }

        (await CountAsync($"select count(*) from discovery.cf_score where traveler_id = '{me}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Only_an_administrator_can_run_the_job_by_hand()
    {
        using var traveler = As(Guid.NewGuid());

        (await traveler.PostAsync(Recompute, null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_schema_holds_no_neighbour_and_no_position()
    {
        var columns = new List<string>();
        await using var connection = new NpgsqlConnection(_connection);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select column_name from information_schema.columns where table_schema = 'discovery' and table_name = 'cf_score' order by column_name", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            columns.Add(reader.GetString(0));
        }

        columns.ShouldBe(["computed_at", "destination", "poi_id", "score", "support", "traveler_id"]);
    }

    [Fact]
    public async Task The_periodic_job_computes_the_scores_by_itself_after_its_startup_delay()
    {
        var me = await SeedCommunityAsync();
        await using var withJob = Host(_connection, startupDelay: "0");

        _ = withJob.Server;

        (await Eventually(async () => await CountAsync($"select count(*) from discovery.cf_score where traveler_id = '{me}'") > 0)).ShouldBeTrue("the job never ran");
    }

    private static async Task<bool> Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(250, Ct);
        }

        return false;
    }
}
