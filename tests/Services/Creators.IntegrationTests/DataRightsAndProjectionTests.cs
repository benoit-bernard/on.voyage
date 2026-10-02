using System.Net.Http.Json;
using System.Text.Json;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Creators.Contracts;
using OnVoyage.Platform.Contracts;
using OnVoyage.TestInfrastructure;

namespace Creators.IntegrationTests;

/// <summary>ADR-0015 for Creators (follows, reports, and the profile of a traveler who is also a creator), and the place directory fed by the Catalog.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class DataRightsAndProjectionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CreatorsHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await CreatorsHost.SharedAsync(postgres);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task DeleteAsync(Guid traveler) =>
        _host.Bus.InvokeAsync(new TravelerDeletionRequestedV1(Guid.CreateVersion7(), DateTimeOffset.UtcNow, traveler, DateTimeOffset.UtcNow), Ct);

    [Fact]
    public async Task Deleting_a_traveler_removes_the_follows_and_unlinks_the_reports_and_confirms_to_platform()
    {
        var creator = await _host.FounderAsync(publish: true);
        var leaving = Guid.NewGuid();
        var staying = Guid.NewGuid();
        using var leavingClient = _host.Traveler(leaving);
        using var stayingClient = _host.Traveler(staying);
        await leavingClient.PutAsync($"/api/creators/v1/me/follows/{creator.Id}", null, Ct);
        await stayingClient.PutAsync($"/api/creators/v1/me/follows/{creator.Id}", null, Ct);
        var report = await CreatorsHost.Read<ReportReceiptDto>(await leavingClient.PostAsJsonAsync("/api/creators/v1/reports", new ReportRequest("creator", creator.Id, "other"), Ct), System.Net.HttpStatusCode.Accepted);

        await DeleteAsync(leaving);

        (await _host.Scalar<long>($"select count(*) from creators.follow where traveler_id = '{leaving}'")).ShouldBe(0);
        (await _host.Scalar<long>($"select count(*) from creators.follow where traveler_id = '{staying}'")).ShouldBe(1);
        (await _host.Scalar<long>($"select count(*) from creators.moderation_case where id = '{report.CaseId}' and reporter_ref is null")).ShouldBe(1); // the report stays, anonymized
        (await _host.Scalar<long>($"select count(*) from creators.moderation_case where reporter_ref = '{leaving}'")).ShouldBe(0);
        (await _host.Queued("platform", "TravelerDataDeletedV1", leaving)).ShouldBe(1);
        (await _host.Queued("platform", "TravelerDataDeletedV1", "creators")).ShouldBeGreaterThanOrEqualTo(1);

        await DeleteAsync(leaving); // asking again is harmless
        (await _host.Scalar<long>($"select count(*) from creators.follow where creator_id = '{creator.Id}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Deleting_a_traveler_who_is_also_a_creator_removes_the_profile_and_withdraws_what_was_published()
    {
        var account = Guid.NewGuid();
        var creator = await _host.FounderAsync(publish: true, account: account);
        var poi = await _host.PoiAsync("Cathédrale de la Major");
        var video = await _host.ContentAsync(creator.Id);
        await _host.LinkAsync(creator.Id, poi, video.Id);
        using var admin = _host.Admin();
        await admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{creator.Id}/tips/{poi}", new SetTipRequest("Montez sur le parvis."), Ct);
        var follower = Guid.NewGuid();
        using var followerClient = _host.Traveler(follower);
        await followerClient.PutAsync($"/api/creators/v1/me/follows/{creator.Id}", null, Ct);

        await DeleteAsync(account);

        foreach (var table in new[] { "creator where id", "content_item where creator_id", "place_link where creator_id", "creator_tip where creator_id", "follow where creator_id" })
        {
            (await _host.Scalar<long>($"select count(*) from creators.{table} = '{creator.Id}'")).ShouldBe(0, table);
        }

        (await followerClient.GetAsync($"/api/creators/v1/creators/{creator.Handle}", Ct)).StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        (await _host.Queued("discovery", "CreatorUnpublishedV1", creator.Id)).ShouldBe(1);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(2); // validated, then removed
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", "\"status\":\"removed\"")).ShouldBeGreaterThanOrEqualTo(1);
        (await _host.Queued("platform", "TravelerDataDeletedV1", account)).ShouldBe(1);
    }

    [Fact]
    public async Task An_export_writes_the_follows_the_reports_and_the_creator_profile_as_one_part()
    {
        var account = Guid.NewGuid();
        var creator = await _host.FounderAsync(publish: true, account: account);
        var other = await _host.FounderAsync(publish: true);
        var poi = await _host.PoiAsync("Abbaye Saint-Victor");
        var video = await _host.ContentAsync(creator.Id);
        await _host.LinkAsync(creator.Id, poi, video.Id, 12);
        using var accountClient = _host.Traveler(account);
        await accountClient.PutAsync($"/api/creators/v1/me/follows/{other.Id}", null, Ct);
        await accountClient.PostAsJsonAsync("/api/creators/v1/reports", new ReportRequest("creator", other.Id, "inaccurate"), Ct);
        var exportId = Guid.NewGuid();

        await _host.Bus.InvokeAsync(new TravelerExportRequestedV1(Guid.CreateVersion7(), DateTimeOffset.UtcNow, exportId, account), Ct);

        var path = Path.Combine(_host.Exports, exportId.ToString("N"), "creators.json");
        File.Exists(path).ShouldBeTrue();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, Ct));
        var root = document.RootElement;
        root.GetProperty("follows").EnumerateArray().Select(follow => follow.GetProperty("handle").GetString()).ShouldBe([other.Handle]);
        root.GetProperty("reports").EnumerateArray().Single().GetProperty("reason").GetString().ShouldBe("inaccurate");
        var profile = root.GetProperty("creator");
        profile.GetProperty("handle").GetString().ShouldBe(creator.Handle);
        profile.GetProperty("termsVersion").GetString().ShouldBe("fondateur");
        profile.GetProperty("contents").GetArrayLength().ShouldBe(1);
        profile.GetProperty("placeLinks").EnumerateArray().Single().GetProperty("startS").GetInt32().ShouldBe(12);
        (await _host.Queued("platform", "TravelerExportPartReadyV1", exportId)).ShouldBe(1);

        // Somebody who follows nobody and is no creator gets an empty part.
        var emptyExport = Guid.NewGuid();
        await _host.Bus.InvokeAsync(new TravelerExportRequestedV1(Guid.CreateVersion7(), DateTimeOffset.UtcNow, emptyExport, Guid.NewGuid()), Ct);
        using var empty = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_host.Exports, emptyExport.ToString("N"), "creators.json"), Ct));
        (empty.RootElement.GetProperty("follows").GetArrayLength(), empty.RootElement.GetProperty("creator").ValueKind).ShouldBe((0, JsonValueKind.Null));
    }

    [Fact]
    public async Task The_directory_follows_the_catalog_by_version_and_keeps_no_position()
    {
        var poi = await _host.PoiAsync("Vieux-Port", version: 5);

        await _host.PoiAsync("Ancien nom", version: 4, id: poi); // stale: ignored
        await _host.PoiAsync("Vieux-Port", version: 5, id: poi); // duplicate: ignored
        (await _host.Scalar<string>($"select name from creators.poi_directory where poi_id = '{poi}'")).ShouldBe("Vieux-Port");

        await _host.PoiAsync("Vieux-Port de Marseille", published: false, version: 6, id: poi);
        (await _host.Scalar<string>($"select name || '/' || is_published::text || '/' || version::text from creators.poi_directory where poi_id = '{poi}'")).ShouldBe("Vieux-Port de Marseille/false/6");
        (await _host.Scalar<string>($"select search_text from creators.poi_directory where poi_id = '{poi}'")).ShouldBe("vieux-port de marseille marseille");
    }

    [Fact]
    public async Task The_projection_event_carries_aliases_and_the_english_name_into_the_directory()
    {
        var poi = Guid.NewGuid();
        await _host.Bus.InvokeAsync(new PoiProjectionChangedV1(Guid.NewGuid(), DateTimeOffset.UtcNow, poi, 1, CreatorsHost.DestinationId("marseille"), "marseille", "ndg", "Notre-Dame de la Garde", "Our Lady of the Guard", ["La Bonne Mère"], "Marseille", 43.2840, 5.3713, 95, false, 0.9f, 4, new Dictionary<string, float>(), ["fragile"], true), Ct);
        using var admin = _host.Admin();

        var byAlias = await CreatorsHost.Read<List<PoiSearchResultDto>>(await admin.GetAsync("/api/creators/v1/admin/places?query=bonne%20mere", Ct));
        var byEnglishName = await CreatorsHost.Read<List<PoiSearchResultDto>>(await admin.GetAsync("/api/creators/v1/admin/places?query=our%20lady", Ct));

        byAlias.Select(item => item.PoiId).ShouldContain(poi);
        byEnglishName.Select(item => item.PoiId).ShouldContain(poi);
        (await _host.Scalar<string>($"select names::text from creators.poi_directory where poi_id = '{poi}'")).ShouldContain("La Bonne Mère");
    }

    [Fact]
    public async Task The_schema_has_no_column_that_can_hold_a_position_and_the_tables_are_the_expected_ones()
    {
        var columns = (await _host.Scalar<string>("select string_agg(distinct column_name, ',') from information_schema.columns where table_schema = 'creators'")).Split(',');
        var tables = (await _host.Scalar<string>("select string_agg(table_name, ',' order by table_name) from information_schema.tables where table_schema = 'creators' and table_name <> '__ef_migrations_history' and table_name not like 'wolverine_%'")).Split(',');

        string[] forbidden = ["lat", "latitude", "lon", "longitude", "lng", "coordinates", "position", "location", "geom", "geog"];
        columns.ShouldNotBeEmpty();
        columns.ShouldAllBe(column => !forbidden.Contains(column, StringComparer.OrdinalIgnoreCase));
        tables.ShouldBe(["connected_account", "content_item", "creator", "creator_tip", "follow", "moderation_case", "place_link", "poi_directory", "unmatched_mention"]);
        (await _host.Scalar<string>("select data_type || '/' || udt_name from information_schema.columns where table_schema = 'creators' and table_name = 'creator' and column_name = 'handle'")).ShouldBe("USER-DEFINED/citext");
    }

    [Fact]
    public async Task The_database_itself_refuses_a_second_handle_in_another_case_and_a_second_content()
    {
        var handle = CreatorsHost.Unique("Case");
        await _host.FounderAsync(handle);

        var act = () => _host.Execute($"insert into creators.creator (id, handle, display_name, languages, specialties, destination_ids, links, status, founding, created_at, updated_at) values ('{Guid.NewGuid()}', '{handle.ToUpperInvariant()}', 'x', '{{}}', '{{}}', '{{}}', '[]', 'draft', true, now(), now())");

        var failure = await Should.ThrowAsync<Npgsql.PostgresException>(act);
        failure.ConstraintName.ShouldBe("ux_creator_handle");
    }
}
