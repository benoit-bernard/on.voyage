using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnVoyage.Insights.Application.Features.Kpis;
using OnVoyage.Insights.Contracts;
using OnVoyage.TestInfrastructure;
using static Insights.IntegrationTests.InsightsHost;

namespace Insights.IntegrationTests;

/// <summary>The KPIs count every event of the database, so this class has a host (and a database) of its own, shared by its tests.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class InsightsKpiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] Iso = ["yyyy-MM-dd"];
    private InsightsHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await CreateAsync(postgres);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static string D(DateOnly day) => day.ToString(Iso[0], CultureInfo.InvariantCulture);

    /// <summary>
    /// 20 personalized and 20 control travelers who all first open the app 10 days ago. The numbers are chosen so each indicator has one
    /// exact expected value (see the assertions).
    /// </summary>
    private async Task<(DateOnly Today, DateOnly Day0)> SeedSyntheticEventsAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var day0 = today.AddDays(-10);
        DateTimeOffset At(DateOnly day, int minute) => new(day.ToDateTime(new TimeOnly(9, 0)).AddMinutes(minute), TimeSpan.Zero);

        for (var i = 0; i < 40; i++)
        {
            var personalized = i < 20;
            var cohort = personalized ? Cohorts.Personalized : Cohorts.Control;
            var n = i % 20; // position in the cohort
            var id = Guid.NewGuid();
            await _host.Consent(id, true);
            using var client = _host.Traveler(id);
            var session0 = Guid.NewGuid();
            var events = new List<EventDto>();

            Dictionary<string, JsonElement> Rec(int rank) => new()
            {
                ["poi_id"] = J($"poi-{rank}"),
                ["surface"] = J("home"),
                ["rank"] = J(rank),
                ["cohort"] = J(cohort),
                ["weights_version"] = J("w1"),
                ["is_exploration"] = J(false),
                ["profile_depth"] = J(n),
            };

            events.Add(Ev("app_open", At(day0, 0), session0, new() { ["cold_start"] = J(true) }));

            // 10 views each; clicks: control 2, personalized 2 for the thin profiles (depth 0 to 9) and 4 for the others, so the click rate rises with depth.
            var clicks = personalized ? (n < 10 ? 2 : 4) : 2;
            for (var r = 0; r < 10; r++)
            {
                events.Add(Ev("recommendation_viewed", At(day0, 1 + r), session0, Rec(r)));
            }

            for (var r = 0; r < clicks; r++)
            {
                events.Add(Ev("recommendation_clicked", At(day0, 20 + r), session0, Rec(r)));
            }

            // Satisfaction (personalized only): 14 likes, 6 dislikes, plus a "meh" that counts for nothing.
            if (personalized)
            {
                events.Add(Ev(n < 14 ? "poi_liked" : "poi_disliked", At(day0, 30), session0, new() { ["poi_id"] = J("poi-1"), ["scope"] = J("poi") }));
                events.Add(Ev("poi_meh", At(day0, 31), session0, new() { ["poi_id"] = J("poi-2"), ["scope"] = J("poi") }));
            }

            // Activation: 18 of 20 personalized and 10 of 20 control travelers listen to a first story, twice. 42 of the 56 listens reach 80 %, the other 14 stop at 50 %.
            var listens = personalized ? n < 18 : n < 10;
            if (listens)
            {
                for (var s = 0; s < 2; s++)
                {
                    var story = new Dictionary<string, JsonElement> { ["story_id"] = J($"story-{s}"), ["version"] = J(1), ["trigger"] = J("manual") };
                    events.Add(Ev("audio_started", At(day0, 40 + (s * 10)), session0, story));
                    var completes = (personalized ? n : n + 18) % 2 == 0 || s == 0;
                    events.Add(Ev("audio_completed", At(day0, 45 + (s * 10)), session0, new(story) { ["percent"] = J(completes ? 100 : 50) }));
                }
            }

            // Creators: every traveler sees 2 creator cards; 12 personalized and 4 control travelers open one (block click rate 0.30 / 0.10);
            // 10 personalized travelers open a creator profile and 3 follow; 5 personalized installs came through a creator link.
            for (var c = 0; c < 2; c++)
            {
                events.Add(Ev("creator_card_viewed", At(day0, 60 + c), session0, new() { ["creator_id"] = J("c1"), ["content_id"] = J($"x{c}"), ["poi_id"] = J("poi-1"), ["surface"] = J("place") }));
            }

            if (personalized ? n < 12 : n < 4)
            {
                events.Add(Ev("creator_content_opened", At(day0, 63), session0, new() { ["creator_id"] = J("c1"), ["content_id"] = J("x0"), ["poi_id"] = J("poi-1"), ["surface"] = J("place") }));
            }

            if (personalized && n < 10)
            {
                events.Add(Ev("creator_profile_viewed", At(day0, 64), session0, new() { ["creator_id"] = J("c1") }));
            }

            if (personalized && n < 3)
            {
                events.Add(Ev("creator_followed", At(day0, 65), session0, new() { ["creator_id"] = J("c1") }));
            }

            if (personalized && n < 5)
            {
                events.Add(Ev("install_attributed", At(day0, 66), session0, new() { ["creator_handle"] = J("marie") }));
            }

            // Two crashes among the sessions that sent usage events.
            if (personalized && n < 2)
            {
                events.Add(Ev("app_crash", At(day0, 55), session0, new() { ["code"] = J("E_PLAYER") }));
            }

            // Retention: day-1 returners (10 personalized, 5 control) and day-7 returners (4 personalized, 0 control).
            if (personalized ? n < 10 : n < 5)
            {
                events.Add(Ev("app_open", At(day0.AddDays(1), 0), Guid.NewGuid(), new() { ["cold_start"] = J(false) }));
            }

            if (personalized && n < 4)
            {
                events.Add(Ev("app_open", At(day0.AddDays(7), 0), Guid.NewGuid(), new() { ["cold_start"] = J(false) }));
            }

            foreach (var chunk in events.Chunk(400))
            {
                (await Post(client, chunk)).Accepted.ShouldBe(chunk.Length);
            }
        }

        return (today, day0);
    }

    private static double V(JsonElement values, string key) => values.GetProperty(key).GetProperty("value").GetDouble();

    private static long S(JsonElement values, string key) => values.GetProperty(key).GetProperty("sample").GetInt64();

    [Fact]
    public async Task The_daily_kpis_are_computed_per_cohort_from_a_synthetic_event_set_and_can_be_filtered_and_exported()
    {
        var (today, day0) = await SeedSyntheticEventsAsync();
        await _host.Bus.InvokeAsync(new ComputeKpisCommand(day0.AddDays(-2), today), Ct);
        await _host.Bus.InvokeAsync(new ComputeKpisCommand(day0.AddDays(-2), today), Ct); // rebuilding is idempotent
        using var admin = _host.Admin();
        var period = $"from={D(day0.AddDays(-2))}&to={D(today)}";

        var json = await admin.GetFromJsonAsync<JsonElement>($"/api/insights/v1/kpis?{period}", Ct);

        // The shape the back office reads: from, to, destination, cohort, values{key:{value,sample}}.
        json.GetProperty("from").GetString().ShouldBe(D(day0.AddDays(-2)));
        json.GetProperty("to").GetString().ShouldBe(D(today));
        json.GetProperty("destination").ValueKind.ShouldBe(JsonValueKind.Null);
        json.GetProperty("cohort").ValueKind.ShouldBe(JsonValueKind.Null);
        var values = json.GetProperty("values");

        // Central hypothesis: 60 / 200 = 0.30 against 40 / 200 = 0.20.
        V(values, "central_ctr_ratio").ShouldBe(1.5, 1e-9);
        S(values, "central_ctr_ratio").ShouldBe(200);
        V(values, "satisfaction").ShouldBe(0.7, 1e-9);
        S(values, "satisfaction").ShouldBe(20);

        // Activation: 28 of the 40 installs. Retention: 15 of 40 at day 1, 4 of 40 at day 7; no installer has seen day 30 yet.
        V(values, "activation").ShouldBe(0.7, 1e-9);
        S(values, "activation").ShouldBe(40);
        V(values, "retention_d1").ShouldBe(15d / 40, 1e-9);
        V(values, "retention_d7").ShouldBe(4d / 40, 1e-9);
        values.TryGetProperty("retention_d30", out _).ShouldBeFalse();

        // 28 travelers listened twice. Sessions with usage events: 40 first days + 15 day-1 returns + 4 day-7 returns.
        V(values, "stories_per_session").ShouldBe(56d / 59, 1e-9);
        S(values, "stories_per_session").ShouldBe(59);
        V(values, "completion").ShouldBe(42d / 56, 1e-9);
        V(values, "crash_rate").ShouldBe(2d / 59, 1e-9);

        // Creators: 16 opens on 80 cards; 3 follows on 10 profile views; 5 attributed installs among the 40.
        V(values, "creator_block_ctr").ShouldBe(16d / 80, 1e-9);
        S(values, "creator_block_ctr").ShouldBe(80);
        V(values, "creator_follow_rate").ShouldBe(0.3, 1e-9);
        V(values, "creator_attributed_installs").ShouldBe(5, 1e-9);
        S(values, "creator_attributed_installs").ShouldBe(40);

        // The strategic KPI: the click rate rises with the profile depth (personalized cohort, 100 views per tranche).
        V(values, "ctr_depth_0_9").ShouldBe(0.2, 1e-9);
        V(values, "ctr_depth_10_49").ShouldBe(0.4, 1e-9);
        values.TryGetProperty("ctr_depth_50_plus", out _).ShouldBeFalse();
        V(values, "profile_depth_median_d7").ShouldBe(9.5, 1e-9);
        S(values, "profile_depth_median_d7").ShouldBe(40);

        // Filtered by cohort.
        var control = (await admin.GetFromJsonAsync<JsonElement>($"/api/insights/v1/kpis?{period}&cohort=control", Ct)).GetProperty("values");
        V(control, "activation").ShouldBe(0.5, 1e-9);
        V(control, "retention_d1").ShouldBe(0.25, 1e-9);
        V(control, "creator_block_ctr").ShouldBe(0.1, 1e-9);
        control.TryGetProperty("creator_follow_rate", out _).ShouldBeFalse(); // no control traveler opened a profile
        control.TryGetProperty("creator_attributed_installs", out _).ShouldBeFalse();
        control.TryGetProperty("central_ctr_ratio", out _).ShouldBeFalse(); // a ratio of two cohorts says nothing about one
        control.TryGetProperty("satisfaction", out _).ShouldBeFalse();      // nobody in the control cohort rated anything

        // No destination is known to the events yet: asking for one gives nothing rather than the global figures.
        var elsewhere = (await admin.GetFromJsonAsync<JsonElement>($"/api/insights/v1/kpis?{period}&destination=marseille", Ct)).GetProperty("values");
        elsewhere.EnumerateObject().ShouldBeEmpty();

        (await admin.GetAsync($"/api/insights/v1/kpis?{period}&cohort=beta", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.GetAsync($"/api/insights/v1/kpis?from={D(today)}&to={D(day0)}", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // CSV export of the stored components.
        var export = await admin.GetAsync($"/api/insights/v1/kpis/export?{period}", Ct);
        export.StatusCode.ShouldBe(HttpStatusCode.OK);
        export.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var lines = (await export.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldBe("date,destination,cohort,metric,value");
        lines.ShouldContain($"{D(day0)},,personalized,rec_viewed,200");
        lines.ShouldContain($"{D(day0)},,control,rec_clicked,40");
        lines.ShouldContain($"{D(day0)},,personalized,installs,20");
        lines.Length.ShouldBeGreaterThan(20);
    }

    [Fact]
    public async Task Travelers_who_refused_the_statistics_do_not_appear_in_the_kpis()
    {
        using var client = _host.Traveler();
        var now = DateTimeOffset.UtcNow;
        await Post(client, Ev("app_open", now.AddMinutes(-3)), Ev("recommendation_viewed", now.AddMinutes(-2), props: new() { ["cohort"] = J("control") }), Ev("app_crash", now.AddMinutes(-1)));
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        await _host.Bus.InvokeAsync(new ComputeKpisCommand(today.AddDays(-1), today), Ct);
        using var admin = _host.Admin();

        var values = (await admin.GetFromJsonAsync<JsonElement>($"/api/insights/v1/kpis?from={D(today.AddDays(-1))}&to={D(today)}", Ct)).GetProperty("values");

        values.EnumerateObject().ShouldBeEmpty();
    }
}
