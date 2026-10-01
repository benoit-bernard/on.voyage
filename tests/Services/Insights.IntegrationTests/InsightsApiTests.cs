using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnVoyage.Insights.Application.Features.Maintenance;
using OnVoyage.Insights.Contracts;
using OnVoyage.Platform.Contracts;
using OnVoyage.TestInfrastructure;
using Wolverine;
using static Insights.IntegrationTests.InsightsHost;

namespace Insights.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class InsightsApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private InsightsHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await SharedAsync(postgres);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // the shared host lives until the end of the run

    private Task<long> ConsentRows(Guid traveler) => _host.Scalar<long>($"select count(*) from insights.consent_projection where traveler_id = '{traveler}'");

    [Fact]
    public async Task Requests_without_a_token_are_refused_and_the_kpis_are_for_admins_only()
    {
        using var anonymous = _host.Factory.CreateClient();
        (await anonymous.PostAsJsonAsync("/api/insights/v1/events", new EventBatchRequest([]), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var traveler = _host.Traveler();
        (await traveler.GetAsync("/api/insights/v1/kpis", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await traveler.GetAsync("/api/insights/v1/kpis/export", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Without_the_statistics_consent_only_the_essential_events_are_stored()
    {
        var id = Guid.NewGuid();
        using var client = _host.Traveler(id);
        var now = DateTimeOffset.UtcNow;

        var result = await Post(client, Ev("app_open", now.AddMinutes(-3)), Ev("audio_started", now.AddMinutes(-2)), Ev("app_crash", now.AddMinutes(-1), props: new() { ["code"] = J("E1") }));

        result.ShouldBe(new EventBatchResponse(1, 0, 2));
        (await _host.Scalar<string>($"select name from insights.event where traveler_ref = '{id}'")).ShouldBe("app_crash");
    }

    [Fact]
    public async Task The_consent_projection_opens_and_closes_the_door_and_ignores_stale_changes()
    {
        var id = Guid.NewGuid();
        using var client = _host.Traveler(id);
        var now = DateTimeOffset.UtcNow;

        await _host.Consent(id, granted: true, now.AddMinutes(-10));
        (await Post(client, Ev("app_open", now.AddMinutes(-9)))).Accepted.ShouldBe(1);

        await _host.Consent(id, granted: false, now.AddMinutes(-8));
        (await Post(client, Ev("app_open", now.AddMinutes(-7)))).ShouldBe(new EventBatchResponse(0, 0, 1));

        // A late-delivered older grant must not reopen it; other kinds of consent are not Insights' business.
        await _host.Consent(id, granted: true, now.AddMinutes(-9));
        await _host.Consent(id, granted: true, now.AddMinutes(-1), kind: "ads_personalization");
        (await Post(client, Ev("app_open", now.AddMinutes(-6)))).Accepted.ShouldBe(0);
        (await ConsentRows(id)).ShouldBe(1);

        await _host.Consent(id, granted: true, now.AddMinutes(-5));
        (await Post(client, Ev("app_open", now.AddMinutes(-4)))).Accepted.ShouldBe(1);
    }

    [Fact]
    public async Task Sending_a_batch_twice_stores_it_once()
    {
        var id = Guid.NewGuid();
        using var client = _host.Traveler(id);
        await _host.Consent(id, true);
        var now = DateTimeOffset.UtcNow;
        EventDto[] batch = [Ev("app_open", now.AddMinutes(-3)), Ev("poi_viewed", now.AddMinutes(-2), props: new() { ["poi_id"] = J("p1"), ["surface"] = J("home") })];

        (await Post(client, batch)).ShouldBe(new EventBatchResponse(2, 0, 0));
        (await Post(client, batch)).ShouldBe(new EventBatchResponse(0, 2, 0));
        (await _host.CountEvents(id)).ShouldBe(2);
    }

    [Fact]
    public async Task An_event_outside_the_catalogue_or_with_a_position_refuses_the_batch()
    {
        var id = Guid.NewGuid();
        using var client = _host.Traveler(id);
        await _host.Consent(id, true);
        var now = DateTimeOffset.UtcNow;

        foreach (var bad in new[] { Ev("secret_event", now), Ev("poi_viewed", now, props: new() { ["lat"] = J(43.29), ["lon"] = J(5.37) }), Ev("app_open", now) with { Platform = "toaster" } })
        {
            var response = await client.PostAsJsonAsync("/api/insights/v1/events", new EventBatchRequest([Ev("app_open", now), bad]), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        }

        (await _host.CountEvents(id)).ShouldBe(0); // the good event sent with each bad one was not stored either
    }

    [Fact]
    public async Task A_batch_may_hold_500_events_and_not_501()
    {
        var id = Guid.NewGuid();
        using var client = _host.Traveler(id);
        await _host.Consent(id, true);
        var now = DateTimeOffset.UtcNow;

        (await client.PostAsJsonAsync("/api/insights/v1/events", new EventBatchRequest([.. Enumerable.Range(0, 501).Select(i => Ev("app_open", now.AddSeconds(-i)))]), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Post(client, [.. Enumerable.Range(0, 500).Select(i => Ev("app_open", now.AddSeconds(-i)))])).Accepted.ShouldBe(500);
    }

    [Fact]
    public async Task The_event_table_is_partitioned_by_month_and_partitions_appear_as_events_arrive()
    {
        var id = Guid.NewGuid();
        using var client = _host.Traveler(id);
        await _host.Consent(id, true);
        var oldMoment = DateTimeOffset.UtcNow.AddMonths(-5);

        (await Post(client, Ev("app_open", oldMoment))).Accepted.ShouldBe(1);

        var partitionName = $"event_y{oldMoment.UtcDateTime:yyyy}m{oldMoment.UtcDateTime:MM}";
        (await _host.Scalar<long>($"select count(*) from pg_inherits i join pg_class c on c.oid = i.inhrelid where c.relname = '{partitionName}'")).ShouldBe(1);
        (await _host.Scalar<string>("select relkind::text from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = 'insights' and c.relname = 'event'")).ShouldBe("p");

        // The migration and the start-up created the previous, current and next two months.
        (await _host.Scalar<long>("select count(*) from pg_inherits i join pg_class p on p.oid = i.inhparent where p.relname = 'event'")).ShouldBeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task The_maintenance_job_drops_old_partitions_and_trims_technical_events()
    {
        var old = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero);
        await _host.Execute("create table insights.event_y2024m01 partition of insights.event for values from ('2024-01-01T00:00:00Z') to ('2024-02-01T00:00:00Z')");
        var traveler = Guid.NewGuid();
        foreach (var moment in new[] { DateTimeOffset.UtcNow.AddDays(-100), DateTimeOffset.UtcNow.AddDays(-10) })
        {
            await _host.Execute(OnVoyage.Insights.Infrastructure.Persistence.PartitionCatalog.CreateStatement(OnVoyage.Insights.Domain.PartitionMonth.Of(moment)));
        }

        await _host.Execute($"""
            insert into insights.event (id, occurred_at, traveler_ref, session_id, name, props, app_version, platform) values
            ('{Guid.NewGuid()}', '{old:O}', '{traveler}', '{Guid.NewGuid()}', 'app_open', '[]', '1', 'ios'),
            ('{Guid.NewGuid()}', now() - interval '100 days', '{traveler}', '{Guid.NewGuid()}', 'app_crash', '[]', '1', 'ios'),
            ('{Guid.NewGuid()}', now() - interval '100 days', '{traveler}', '{Guid.NewGuid()}', 'app_open', '[]', '1', 'ios'),
            ('{Guid.NewGuid()}', now() - interval '10 days', '{traveler}', '{Guid.NewGuid()}', 'app_crash', '[]', '1', 'ios')
            """);
        await _host.Execute("insert into insights.daily_kpi (date, destination_id, cohort, metric, value) values ('2024-01-15', '', 'control', 'sessions', 3)");

        var report = await _host.Bus.InvokeAsync<MaintenanceReport>(new RunMaintenanceCommand(), Ct);

        report.PartitionsDropped.ShouldBe(1);
        report.ExpiredTechnicalEventsDeleted.ShouldBe(1);
        (await _host.Scalar<long>("select count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = 'insights' and c.relname = 'event_y2024m01'")).ShouldBe(0);
        (await _host.Scalar<string>($"select string_agg(name || ':' || (now() - occurred_at > interval '50 days')::text, ',' order by name, occurred_at) from insights.event where traveler_ref = '{traveler}'"))
            .ShouldBe("app_crash:false,app_open:true");
        (await _host.Scalar<long>("select count(*) from insights.daily_kpi where date = '2024-01-15'")).ShouldBe(1); // anonymous aggregates are kept

        // Running it again changes nothing.
        (await _host.Bus.InvokeAsync<MaintenanceReport>(new RunMaintenanceCommand(), Ct)).ShouldBe(new MaintenanceReport(0, 0, 0, 0));
    }

    private Task<long> QueuedFor(string queue, string messageType) =>
        _host.Scalar<long>($"select count(*) from wolverine_queues.wolverine_queue_{queue} where message_type like '%{messageType}%'");

    [Fact]
    public async Task A_deletion_request_removes_everything_held_and_confirms_to_platform()
    {
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        using var client = _host.Traveler(id);
        using var otherClient = _host.Traveler(other);
        await _host.Consent(id, true);
        await _host.Consent(other, true);
        var now = DateTimeOffset.UtcNow;
        await Post(client, Ev("app_open", now.AddMinutes(-3)), Ev("app_crash", now.AddMinutes(-2)));
        await Post(otherClient, Ev("app_open", now.AddMinutes(-3)));

        await _host.Bus.InvokeAsync(new TravelerDeletionRequestedV1(id, now), Ct);

        (await _host.CountEvents(id)).ShouldBe(0);
        (await ConsentRows(id)).ShouldBe(0);
        (await _host.CountEvents(other)).ShouldBe(1);
        (await ConsentRows(other)).ShouldBe(1);

        // Platform receives the confirmation; repeating the request is harmless.
        await Eventually(async () => (await QueuedFor("platform", nameof(TravelerDataDeletedV1))).ShouldBeGreaterThanOrEqualTo(1));
        await _host.Bus.InvokeAsync(new TravelerDeletionRequestedV1(id, now), Ct);
        (await _host.CountEvents(id)).ShouldBe(0);
    }

    [Fact]
    public async Task An_export_request_writes_the_travelers_events_as_one_json_part()
    {
        var id = Guid.NewGuid();
        using var client = _host.Traveler(id);
        await _host.Consent(id, true);
        var now = DateTimeOffset.UtcNow;
        await Post(client, Ev("app_open", now.AddMinutes(-3), props: new() { ["cold_start"] = J(true) }), Ev("poi_viewed", now.AddMinutes(-2), props: new() { ["poi_id"] = J("p1") }));
        await Post(_host.Traveler(), Ev("app_crash", now.AddMinutes(-1)));
        var exportId = Guid.NewGuid();

        await _host.Bus.InvokeAsync(new TravelerExportRequestedV1(exportId, id), Ct);

        var path = Path.Combine(_host.Exports, "exports", exportId.ToString("D"), "insights.json");
        File.Exists(path).ShouldBeTrue();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, Ct));
        document.RootElement.GetProperty("analyticsConsent").GetBoolean().ShouldBeTrue();
        var events = document.RootElement.GetProperty("events").EnumerateArray().ToList();
        events.Select(e => e.GetProperty("name").GetString()).ShouldBe(["app_open", "poi_viewed"]);
        events[0].GetProperty("props").GetProperty("cold_start").GetBoolean().ShouldBeTrue();
        await Eventually(async () => (await QueuedFor("platform", nameof(TravelerExportPartReadyV1))).ShouldBeGreaterThanOrEqualTo(1));
    }

    [Fact]
    public async Task The_schema_has_no_column_that_can_hold_a_position()
    {
        var columns = (await _host.Scalar<string>("select string_agg(distinct column_name, ',') from information_schema.columns where table_schema = 'insights'")).Split(',');

        columns.ShouldNotBeEmpty();
        string[] forbidden = ["lat", "latitude", "lon", "longitude", "lng", "coordinates", "position", "location", "geom", "geog"];
        columns.ShouldAllBe(column => !forbidden.Contains(column, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task Eventually(Func<Task> assertion)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await assertion();
                return;
            }
            catch (Exception) when (attempt < 40)
            {
                await Task.Delay(100, Ct);
            }
        }
    }
}
