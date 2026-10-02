using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnVoyage.Factory.Application.Features.Videos;
using OnVoyage.TestInfrastructure;

namespace Factory.IntegrationTests;

/// <summary>
/// T-407: the YouTube quota seen from the back-office API. The search itself is the deterministic fake; what is real here is the counting of
/// units per quota day, the cache of recent searches and the overview of the selected videos, on PostgreSQL.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class VideoQuotaTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Admin = "/api/factory/v1/admin";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private FactoryHarness _f = null!;
    private Guid _place;

    public async ValueTask InitializeAsync()
    {
        _f = await FactoryHarness.StartAsync(postgres);
        _place = Guid.CreateVersion7();
        await _f.ExecuteAsync($$"""
            insert into factory.place (id, destination_slug, slug, name, location, qid, osm_type, osm_id, osm_tags, status, annual_pageviews, importance_score, popularity_percentile,
                hidden_gem, crowd_offpeak, crowd_shoulder, crowd_peak, classification_outcome, classification_confidence, editorially_saturated, fragile, access_regulated,
                published_version, source, source_license, source_url, import_run_id, retrieved_at, created_at, updated_at)
            values ('{{_place}}', 'marseille', 'notre-dame-de-la-garde', 'Notre-Dame de la Garde', st_makepoint(5.37, 43.28)::geography, 'QGARDE', 'W', 1, '{}', 'Candidate', 1000, 90, 50,
                false, 1, 2, 3, 'Rules', 1, false, false, false, 0, 'osm', 'ODbL-1.0', 'https://www.openstreetmap.org/way/1', gen_random_uuid(), now(), now(), now())
            """);
    }

    public async ValueTask DisposeAsync()
    {
        if (_f is not null)
        {
            await _f.DisposeAsync();
        }
    }

    private async Task<JsonElement> QuotaAsync() => await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/videos/quota", Ct);

    private static DateOnly QuotaDay => VideoQuotaClock.DayOf(DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_search_is_counted_once_and_asked_again_it_is_answered_from_the_cache_for_free()
    {
        (await QuotaAsync()).GetProperty("unitsUsed").GetInt32().ShouldBe(0);

        var first = await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/videos/search?q=Notre-Dame%20%20de%20la%20Garde", Ct);
        var again = await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/videos/search?q=notre-dame%20de%20la%20garde%20", Ct);

        again.GetArrayLength().ShouldBe(first.GetArrayLength());
        first.GetArrayLength().ShouldBe(3);
        _f.Content.Videos.Searches.Count.ShouldBe(1, "the second search never reached YouTube");
        var quota = await QuotaAsync();
        quota.GetProperty("unitsUsed").GetInt32().ShouldBe(100);
        quota.GetProperty("dailyUnits").GetInt32().ShouldBe(10_000);
        quota.GetProperty("remaining").GetInt32().ShouldBe(9_900);
        quota.GetProperty("searchesLeft").GetInt32().ShouldBe(99);
        quota.GetProperty("resetsAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        (await _f.CountAsync("select count(*) from factory.youtube_search")).ShouldBe(1);
    }

    [Fact]
    public async Task When_the_day_is_nearly_full_a_new_search_is_refused_with_a_429_but_a_cached_one_still_answers()
    {
        (await _f.Admin.GetAsync($"{Admin}/videos/search?q=garde", Ct)).EnsureSuccessStatusCode();
        await _f.ExecuteAsync($"update factory.youtube_usage set units = 9950 where day = '{QuotaDay:yyyy-MM-dd}'");

        var refused = await _f.Admin.GetAsync($"{Admin}/videos/search?q=autre%20recherche", Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("type").GetString().ShouldBe("https://on.voyage/problems/youtube_quota_exhausted");
        problem.GetProperty("title").GetString()!.ShouldContain("quota");
        _f.Content.Videos.Searches.Count.ShouldBe(1);
        (await _f.Admin.GetAsync($"{Admin}/videos/search?q=GARDE", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var quota = await QuotaAsync();
        quota.GetProperty("remaining").GetInt32().ShouldBe(50);
        quota.GetProperty("searchesLeft").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task The_quota_of_another_day_does_not_count_today()
    {
        await _f.ExecuteAsync("insert into factory.youtube_usage (day, units) values (current_date - 3, 10000)");

        (await _f.Admin.GetAsync($"{Admin}/videos/search?q=garde", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await QuotaAsync()).GetProperty("unitsUsed").GetInt32().ShouldBe(100);
    }

    [Fact]
    public async Task Choosing_and_removing_videos_is_listed_with_the_place_and_costs_one_unit_per_lookup()
    {
        var picked = await _f.Admin.PostAsJsonAsync($"{Admin}/places/{_place}/videos", new { videoId = "abcdefghijk" }, Ct);
        picked.StatusCode.ShouldBe(HttpStatusCode.OK, await picked.Content.ReadAsStringAsync(Ct));
        (await _f.Admin.PostAsJsonAsync($"{Admin}/places/{_place}/videos", new { videoId = "ZYXWVUTSRQP" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await QuotaAsync()).GetProperty("unitsUsed").GetInt32().ShouldBe(2);

        var overview = (await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/videos", Ct)).EnumerateArray().ToList();
        overview.Count.ShouldBe(2);
        overview.ShouldAllBe(item => item.GetProperty("placeName").GetString() == "Notre-Dame de la Garde" && item.GetProperty("destination").GetString() == "marseille");
        overview.Select(item => item.GetProperty("video").GetProperty("url").GetString()).Order(StringComparer.Ordinal).ShouldBe(["https://www.youtube.com/watch?v=ZYXWVUTSRQP", "https://www.youtube.com/watch?v=abcdefghijk"]);

        (await _f.Admin.DeleteAsync($"{Admin}/places/{_place}/videos/abcdefghijk", Ct)).EnsureSuccessStatusCode();
        (await _f.Admin.GetFromJsonAsync<JsonElement>($"{Admin}/videos", Ct)).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_full_day_stops_the_selection_too_and_nothing_is_stored()
    {
        await _f.ExecuteAsync($"insert into factory.youtube_usage (day, units) values ('{QuotaDay:yyyy-MM-dd}', 10000)");

        var refused = await _f.Admin.PostAsJsonAsync($"{Admin}/places/{_place}/videos", new { videoId = "abcdefghijk" }, Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await _f.CountAsync("select count(*) from factory.place_video")).ShouldBe(0);
    }

    [Fact]
    public async Task The_quota_and_the_overview_are_for_administrators_only()
    {
        using var traveler = _f.ApiClient(TestTokens.Mint());

        (await traveler.GetAsync($"{Admin}/videos/quota", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await traveler.GetAsync($"{Admin}/videos", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
