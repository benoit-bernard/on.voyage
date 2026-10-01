using System.Net;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using Npgsql;
using OnVoyage.Platform.Contracts;
using OnVoyage.TestInfrastructure;

namespace Platform.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class PlatformApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string AdminEmail = "boss@example.org";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlatformHarness _p = null!;

    public async ValueTask InitializeAsync() => _p = await PlatformHarness.StartAsync(postgres, null, AdminEmail);

    public async ValueTask DisposeAsync() => await _p.DisposeAsync();

    private async Task<AuthSessionDto> AdminAsync() => await _p.SignInAsync(AdminEmail, await _p.AnonymousAsync());

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_p.Connection);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    // ---- configuration

    [Fact]
    public async Task Client_config_is_public_and_serves_annexe_e_without_server_only_keys()
    {
        var config = await _p.Client.GetFromJsonAsync<ClientConfigDto>("/api/platform/v1/config?platform=android&appVersion=1.0.0", Ct);

        config!.Config.Keys.ShouldContain("reco");
        config.Config.Keys.ShouldContain("app");
        config.Config.Keys.ShouldNotContain("security");
        config.Config.Keys.ShouldNotContain("deletion");
        config.Config["reco"].GetProperty("control_cohort_percent").GetInt32().ShouldBe(20);
        config.Flags["control_cohort"].ShouldBeTrue();
        config.Flags.Count.ShouldBe(11);
    }

    [Fact]
    public async Task Admin_endpoints_need_an_admin_token()
    {
        (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/admin/flags")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var traveler = await _p.AnonymousAsync();
        (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/admin/flags", traveler)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var verifiedNonAdmin = await _p.SignInAsync("someone@example.org", await _p.AnonymousAsync());
        (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/admin/flags", verifiedNonAdmin)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var admin = await AdminAsync();
        admin.Roles.ShouldContain("admin");
        (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/admin/flags", admin)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_admin_lists_every_key_and_reads_the_history_of_a_key_newest_first()
    {
        var admin = await AdminAsync();
        (await _p.SendAsync(HttpMethod.Put, "/api/platform/v1/admin/config/reco", admin, new { value = new { radius_m = 777 } })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var list = (await (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/admin/config", admin)).Content.ReadFromJsonAsync<List<ConfigEntryDto>>(Ct))!;
        list.Select(entry => entry.Key).ShouldContain("reco");
        list.Select(entry => entry.Key).ShouldContain("security");
        list.Select(entry => entry.Key).ShouldBe(list.Select(entry => entry.Key).Order(StringComparer.Ordinal));

        var history = (await (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/admin/config/reco/history", admin)).Content.ReadFromJsonAsync<List<ConfigEntryDto>>(Ct))!;
        history.Count.ShouldBeGreaterThanOrEqualTo(2);
        history[0].Version.ShouldBe(history[1].Version + 1);
        history[0].Value.GetProperty("radius_m").GetInt32().ShouldBe(777);
        history[0].UpdatedBy.ShouldStartWith("admin:");

        (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/admin/config/nothing_here/history", admin)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/admin/config", await _p.AnonymousAsync())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_writes_in_platform_are_journaled_with_what_was_asked_and_reads_are_not()
    {
        var admin = await AdminAsync();
        await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/admin/flags", admin);
        (await _p.SendAsync(HttpMethod.Put, "/api/platform/v1/admin/config/reco", admin, new { value = new { radius_m = 4321 } })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _p.SendAsync(HttpMethod.Put, "/api/platform/v1/admin/config/Bad-Key", admin, new { value = new { x = 1 } })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var journal = await (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/admin/audit?service=platform", admin)).Content.ReadFromJsonAsync<List<AdminActionDto>>(Ct);

        journal!.Count.ShouldBe(2, "the read is not journaled; the audit read itself is a read too");
        journal.ShouldAllBe(entry => entry.Service == "platform" && entry.Actor == admin.TravelerId.ToString());
        journal.Select(entry => entry.Status).Order().ShouldBe([200, 400]);
        journal.Single(entry => entry.Status == 200).Summary!.ShouldContain("4321");
        journal.Single(entry => entry.Status == 200).Target.ShouldBe("/api/platform/v1/admin/config/reco");

        (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/admin/audit", await _p.AnonymousAsync())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_same_event_delivered_twice_is_journaled_once()
    {
        await using var scope = _p.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<OnVoyage.Platform.Application.Features.Audit.IAdminAuditStore>();
        var action = new AdminActionRecordedV1(Guid.CreateVersion7(), DateTimeOffset.UtcNow, "factory", "actor-1", "POST publish", "/x", 200, "id=x");

        (await store.AddAsync(action, Ct)).ShouldBeTrue();
        (await store.AddAsync(action, Ct)).ShouldBeFalse();

        (await store.ListAsync(10, "factory", null, Ct)).Count(entry => entry.EventId == action.EventId).ShouldBe(1);
    }

    [Fact]
    public async Task A_forged_admin_token_is_refused()
    {
        var forged = TestTokens.Mint(roles: ["admin"], anonymous: false, secret: "an-attacker-secret-0123456789abcdef-0123456789");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/platform/v1/admin/flags");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", forged);

        (await _p.Client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Edge_scope_is_internal_only_and_returns_just_security()
    {
        var traveler = await _p.AnonymousAsync();
        (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/config?scope=edge", traveler)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/config?scope=edge")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var internalRequest = new HttpRequestMessage(HttpMethod.Get, "/api/platform/v1/config?scope=edge");
        internalRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestTokens.Mint(roles: ["internal"], anonymous: false));
        var response = await _p.Client.SendAsync(internalRequest, Ct);
        var config = await response.Content.ReadFromJsonAsync<ClientConfigDto>(Ct);

        config!.Config.Keys.ShouldBe(["security"]);
        config.Config["security"].GetProperty("blocked_user_agents").GetArrayLength().ShouldBeGreaterThan(10);
    }

    [Fact]
    public async Task Config_changes_are_versioned_audited_by_actor_and_kept_in_history()
    {
        var admin = await AdminAsync();

        var put = await _p.SendAsync(HttpMethod.Put, "/api/platform/v1/admin/config/audio", admin, new { value = new { resume_after_interruption_s = 45 } });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entry = await put.Content.ReadFromJsonAsync<ConfigEntryDto>(Ct);
        entry!.Version.ShouldBe(2);
        entry.UpdatedBy.ShouldBe($"admin:{admin.TravelerId}");

        var client = await _p.Client.GetFromJsonAsync<ClientConfigDto>("/api/platform/v1/config", Ct);
        client!.Config["audio"].GetProperty("resume_after_interruption_s").GetInt32().ShouldBe(45);
        (await CountAsync("select count(*) from platform.remote_config_history where key = 'audio'")).ShouldBe(2);
    }

    [Fact]
    public async Task Invalid_keys_are_rejected_and_unknown_keys_are_404()
    {
        var admin = await AdminAsync();

        (await _p.SendAsync(HttpMethod.Put, "/api/platform/v1/admin/config/brand_new", admin, new { value = 7 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var bad = await _p.SendAsync(HttpMethod.Put, "/api/platform/v1/admin/config/Bad-Key", admin, new { value = 7 });

        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync(Ct)).ShouldContain("https://on.voyage/problems/validation");
        (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/admin/config/missing", admin)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Flags_follow_platform_version_and_rollout_rules()
    {
        var admin = await AdminAsync();
        (await _p.SendAsync(HttpMethod.Put, "/api/platform/v1/admin/flags/car_mode", admin,
            new { enabled = true, rolloutPercent = 100, platforms = new[] { "android" }, minAppVersion = "1.2.0" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        async Task<bool> CarMode(string query) =>
            (await _p.Client.GetFromJsonAsync<ClientConfigDto>($"/api/platform/v1/config?{query}", Ct))!.Flags["car_mode"];

        (await CarMode("platform=android&appVersion=1.2.0")).ShouldBeTrue();
        (await CarMode("platform=android&appVersion=1.1.0")).ShouldBeFalse();
        (await CarMode("platform=ios&appVersion=1.2.0")).ShouldBeFalse();

        (await _p.SendAsync(HttpMethod.Put, "/api/platform/v1/admin/flags/unknown_flag", admin, new { enabled = true, rolloutPercent = 100 })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _p.SendAsync(HttpMethod.Put, "/api/platform/v1/admin/flags/car_mode", admin, new { enabled = true, rolloutPercent = 400 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Partial_rollout_buckets_travelers_by_the_id_in_their_token()
    {
        var admin = await AdminAsync();
        await _p.SendAsync(HttpMethod.Put, "/api/platform/v1/admin/flags/surprise_me", admin, new { enabled = true, rolloutPercent = 50 });

        async Task<bool> For(string? token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/platform/v1/config");
            if (token is not null)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }

            return (await (await _p.Client.SendAsync(request, Ct)).Content.ReadFromJsonAsync<ClientConfigDto>(Ct))!.Flags["surprise_me"];
        }

        (await For(null)).ShouldBeFalse();
        var tokens = Enumerable.Range(0, 40).Select(i => TestTokens.Mint(new Guid(i, 1, 2, [3, 4, 5, 6, 7, 8, 9, 10]))).ToArray();
        var results = new List<bool>();
        foreach (var token in tokens)
        {
            results.Add(await For(token));
        }

        results.ShouldContain(true);
        results.ShouldContain(false);
        (await For(tokens[0])).ShouldBe(results[0]);
    }

    // ---- consents (identity comes from the token)

    [Fact]
    public async Task Consents_need_a_session_and_round_trip_for_the_token_owner()
    {
        (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/me/consents")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var me = await _p.AnonymousAsync();
        var initial = await (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/me/consents", me)).Content.ReadFromJsonAsync<List<ConsentDto>>(Ct);
        initial!.ShouldAllBe(consent => !consent.Granted);

        (await _p.SendAsync(HttpMethod.Put, "/api/platform/v1/me/consents/analytics", me, new { granted = true, textVersion = "2026-10" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/me/consents", me)).Content.ReadFromJsonAsync<List<ConsentDto>>(Ct);
        after.ShouldNotBeNull().Single(consent => consent.Kind == "analytics").Granted.ShouldBeTrue();

        var someoneElse = await _p.AnonymousAsync();
        var theirs = await (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/me/consents", someoneElse)).Content.ReadFromJsonAsync<List<ConsentDto>>(Ct);
        theirs!.ShouldAllBe(consent => !consent.Granted);

        (await _p.SendAsync(HttpMethod.Put, "/api/platform/v1/me/consents/marketing", me, new { granted = true, textVersion = "x" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Health_is_open_and_includes_the_database()
    {
        (await _p.Client.GetAsync("/health", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
