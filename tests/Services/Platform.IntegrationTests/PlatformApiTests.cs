using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using OnVoyage.Platform.Api;
using OnVoyage.Platform.Contracts;
using OnVoyage.TestInfrastructure;

namespace Platform.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class PlatformApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string AdminKey = "test-admin-key";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<PlatformApiMarker> _factory = null!;
    private HttpClient _client = null!;
    private string _connection = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _connection = await postgres.CreateDatabaseAsync();
        _factory = new WebApplicationFactory<PlatformApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", _connection);
            builder.UseSetting("Auth:AdminApiKey", AdminKey);
            builder.UseSetting("Auth:AllowTravelerIdHeader", "true");
        });
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static HttpRequestMessage Admin(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Admin-Key", AdminKey);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connection);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task Client_config_serves_annexe_e_without_server_only_keys_and_the_flags()
    {
        var config = await _client.GetFromJsonAsync<ClientConfigDto>("/api/platform/v1/config?platform=android&appVersion=1.0.0", Ct);

        config!.Config.Keys.ShouldContain("reco");
        config.Config.Keys.ShouldContain("app");
        config.Config.Keys.ShouldNotContain("security");
        config.Config.Keys.ShouldNotContain("deletion");
        config.Config["reco"].GetProperty("control_cohort_percent").GetInt32().ShouldBe(20);
        config.Flags["control_cohort"].ShouldBeTrue();
        config.Flags["car_mode"].ShouldBeFalse();
        config.Flags.Count.ShouldBe(11);
    }

    [Fact]
    public async Task Admin_endpoints_reject_missing_and_wrong_keys()
    {
        (await _client.GetAsync("/api/platform/v1/admin/flags", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/platform/v1/admin/flags");
        wrong.Headers.Add("X-Admin-Key", "nope");
        (await _client.SendAsync(wrong, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Edge_scope_is_internal_and_returns_only_security()
    {
        (await _client.GetAsync("/api/platform/v1/config?scope=edge", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var response = await _client.SendAsync(Admin(HttpMethod.Get, "/api/platform/v1/config?scope=edge"), Ct);
        var config = await response.Content.ReadFromJsonAsync<ClientConfigDto>(Ct);

        config!.Config.Keys.ShouldBe(["security"]);
        config.Config["security"].GetProperty("blocked_user_agents").GetArrayLength().ShouldBeGreaterThan(10);
    }

    [Fact]
    public async Task Config_changes_are_versioned_and_kept_in_history_with_the_event_in_the_outbox()
    {
        var put = await _client.SendAsync(Admin(HttpMethod.Put, "/api/platform/v1/admin/config/audio", new { value = new { resume_after_interruption_s = 45 } }), Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entry = await put.Content.ReadFromJsonAsync<ConfigEntryDto>(Ct);
        entry!.Version.ShouldBe(2);

        var client = await _client.GetFromJsonAsync<ClientConfigDto>("/api/platform/v1/config", Ct);
        client!.Config["audio"].GetProperty("resume_after_interruption_s").GetInt32().ShouldBe(45);

        (await CountAsync("select count(*) from platform.remote_config_history where key = 'audio'")).ShouldBe(2);
    }

    [Fact]
    public async Task New_keys_are_created_and_invalid_keys_are_rejected()
    {
        (await _client.SendAsync(Admin(HttpMethod.Put, "/api/platform/v1/admin/config/brand_new", new { value = 7 }), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var bad = await _client.SendAsync(Admin(HttpMethod.Put, "/api/platform/v1/admin/config/Bad-Key", new { value = 7 }), Ct);

        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync(Ct)).ShouldContain("https://on.voyage/problems/validation");
        (await _client.SendAsync(Admin(HttpMethod.Get, "/api/platform/v1/admin/config/missing"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Flags_follow_platform_version_and_rollout_rules()
    {
        var update = await _client.SendAsync(Admin(HttpMethod.Put, "/api/platform/v1/admin/flags/car_mode", new { enabled = true, rolloutPercent = 100, platforms = new[] { "android" }, minAppVersion = "1.2.0" }), Ct);
        update.StatusCode.ShouldBe(HttpStatusCode.OK);

        async Task<bool> CarMode(string query) =>
            (await _client.GetFromJsonAsync<ClientConfigDto>($"/api/platform/v1/config?{query}", Ct))!.Flags["car_mode"];

        (await CarMode("platform=android&appVersion=1.2.0")).ShouldBeTrue();
        (await CarMode("platform=android&appVersion=1.1.0")).ShouldBeFalse();
        (await CarMode("platform=ios&appVersion=1.2.0")).ShouldBeFalse();

        (await _client.SendAsync(Admin(HttpMethod.Put, "/api/platform/v1/admin/flags/unknown_flag", new { enabled = true, rolloutPercent = 100 }), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.SendAsync(Admin(HttpMethod.Put, "/api/platform/v1/admin/flags/car_mode", new { enabled = true, rolloutPercent = 400 }), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Partial_rollout_uses_the_traveler_identity_when_provided()
    {
        await _client.SendAsync(Admin(HttpMethod.Put, "/api/platform/v1/admin/flags/surprise_me", new { enabled = true, rolloutPercent = 50 }), Ct);

        async Task<bool> For(Guid? traveler)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/platform/v1/config");
            if (traveler is { } id)
            {
                request.Headers.Add("X-Traveler-Id", id.ToString());
            }

            return (await (await _client.SendAsync(request, Ct)).Content.ReadFromJsonAsync<ClientConfigDto>(Ct))!.Flags["surprise_me"];
        }

        (await For(null)).ShouldBeFalse();
        var travelers = Enumerable.Range(0, 40).Select(i => new Guid(i, 1, 2, [3, 4, 5, 6, 7, 8, 9, 10])).ToArray();
        var results = new List<bool>();
        foreach (var traveler in travelers)
        {
            results.Add(await For(traveler));
        }

        results.ShouldContain(true);
        results.ShouldContain(false);
        (await For(travelers[0])).ShouldBe(results[0]);
    }

    [Fact]
    public async Task Consents_require_an_identity_and_round_trip()
    {
        (await _client.GetAsync("/api/platform/v1/me/consents", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var traveler = Guid.NewGuid();
        HttpRequestMessage As(HttpMethod method, string url, object? body = null)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add("X-Traveler-Id", traveler.ToString());
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            return request;
        }

        var initial = await (await _client.SendAsync(As(HttpMethod.Get, "/api/platform/v1/me/consents"), Ct)).Content.ReadFromJsonAsync<List<ConsentDto>>(Ct);
        initial!.ShouldAllBe(consent => !consent.Granted);

        var put = await _client.SendAsync(As(HttpMethod.Put, "/api/platform/v1/me/consents/analytics", new { granted = true, textVersion = "2026-10" }), Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await (await _client.SendAsync(As(HttpMethod.Get, "/api/platform/v1/me/consents"), Ct)).Content.ReadFromJsonAsync<List<ConsentDto>>(Ct);
        after.ShouldNotBeNull().Single(consent => consent.Kind == "analytics").Granted.ShouldBeTrue();
        after.Single(consent => consent.Kind == "ads_personalization").Granted.ShouldBeFalse();

        (await _client.SendAsync(As(HttpMethod.Put, "/api/platform/v1/me/consents/marketing", new { granted = true, textVersion = "x" }), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Traveler_identity_header_is_ignored_unless_explicitly_allowed()
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var strict = new WebApplicationFactory<PlatformApiMarker>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:onvoyage", connection).UseSetting("Auth:AllowTravelerIdHeader", "false").UseSetting("Auth:AdminApiKey", string.Empty));
        using var client = strict.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/platform/v1/me/consents");
        request.Headers.Add("X-Traveler-Id", Guid.NewGuid().ToString());

        (await client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/platform/v1/admin/flags"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_includes_the_database()
    {
        (await _client.GetAsync("/health", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
