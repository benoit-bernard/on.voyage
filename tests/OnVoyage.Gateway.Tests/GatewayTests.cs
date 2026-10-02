using System.Net;
using System.Text;
using System.Text.Json;
using OnVoyage.Gateway.Edge;
using OnVoyage.TestInfrastructure;

namespace OnVoyage.Gateway.Tests;

public sealed class GatewayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PoisUrl = "/api/catalog/v1/destinations/marseille/pois";

    // ---- JWT validation (SEC-02)

    [Fact]
    public async Task Protected_routes_need_a_valid_token_and_the_token_is_forwarded()
    {
        await using var g = await GatewayHarness.StartAsync();
        var forged = TestTokens.Mint(secret: "an-attacker-secret-0123456789abcdef-0123456789");
        var expired = TestTokens.Mint(lifetime: TimeSpan.FromMinutes(-5), now: DateTimeOffset.UtcNow.AddHours(-1));
        var token = TestTokens.Mint();

        (await g.GetAsync(PoisUrl)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await g.GetAsync(PoisUrl, "garbage")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await g.GetAsync(PoisUrl, forged)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await g.GetAsync(PoisUrl, expired)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        g.Stub.AuthorizationSeen.ShouldBeEmpty();

        (await g.GetAsync(PoisUrl, token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        g.Stub.AuthorizationSeen.ShouldBe([$"Bearer {token}"]);
    }

    [Fact]
    public async Task Story_audio_is_public_read_only_and_keeps_byte_ranges_so_players_can_seek()
    {
        await using var g = await GatewayHarness.StartAsync();
        const string Url = "/media/audio/6ec53cce/fr/standard/v1_marin_main.mp3";

        var whole = await g.GetAsync(Url);
        whole.StatusCode.ShouldBe(HttpStatusCode.OK);
        whole.Content.Headers.ContentType!.MediaType.ShouldBe("audio/mpeg");
        (await whole.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length.ShouldBe(256);

        var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 99);
        var partial = await g.Client.SendAsync(request, TestContext.Current.CancellationToken);
        partial.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        (await partial.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length.ShouldBe(100);

        (await g.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Url), TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await g.Client.PostAsync(Url, new StringContent("x"), TestContext.Current.CancellationToken)).IsSuccessStatusCode.ShouldBeFalse("media is read-only");
        g.Stub.AuthorizationSeen.ShouldBeEmpty("media is served without a session and never reaches the API routes");
    }

    [Fact]
    public async Task Public_platform_routes_are_open_but_the_rest_of_platform_is_not()
    {
        await using var g = await GatewayHarness.StartAsync();

        (await g.GetAsync("/api/platform/v1/config")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await g.GetAsync("/api/platform/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await g.GetAsync("/api/platform/v1/admin/flags")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await g.GetAsync("/api/platform/v1/me", TestTokens.Mint())).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Creators_admin_routes_need_the_admin_role_and_traveler_routes_any_session()
    {
        await using var g = await GatewayHarness.StartAsync();
        var traveler = TestTokens.Mint();
        var creator = TestTokens.Mint(roles: ["creator"]);
        var admin = TestTokens.Mint(roles: ["admin"]);

        // Traveler routes: any session, but a session.
        (await g.GetAsync("/api/creators/v1/creators/marie")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await g.GetAsync("/api/creators/v1/creators/marie", traveler)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await g.GetAsync("/api/creators/v1/me/follows", traveler)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Admin routes: the admin role, stopped at the edge for everyone else.
        (await g.GetAsync("/api/creators/v1/admin/creators")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await g.GetAsync("/api/creators/v1/admin/creators", traveler)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await g.GetAsync("/api/creators/v1/admin/moderation", creator)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await g.GetAsync("/api/creators/v1/admin/creators", admin)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await g.GetAsync("/api/creators/v1/admin/moderation", admin)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Studio_routes_need_the_creator_role_except_sign_up_which_needs_a_verified_account()
    {
        await using var g = await GatewayHarness.StartAsync();
        var anonymous = TestTokens.Mint();
        var account = TestTokens.Mint(anonymous: false);
        var creator = TestTokens.Mint(anonymous: false, roles: ["creator"]);

        (await g.GetAsync("/api/creators/v1/studio/profile")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await g.GetAsync("/api/creators/v1/studio/profile", anonymous)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await g.GetAsync("/api/creators/v1/studio/profile", account)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await g.GetAsync("/api/creators/v1/studio/profile", creator)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await g.GetAsync("/api/creators/v1/studio/contents", account)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Before the role exists, a verified e-mail is enough to see where one stands and to sign up; an anonymous session is not.
        (await g.GetAsync("/api/creators/v1/studio/registration", anonymous)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await g.GetAsync("/api/creators/v1/studio/registration", account)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_endpoints_stay_open()
    {
        await using var g = await GatewayHarness.StartAsync();

        (await g.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await g.GetAsync("/alive")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- crawler blocking (F-24)

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; GPTBot/1.2; +https://openai.com/gptbot)")]
    [InlineData("ClaudeBot/1.0")]
    [InlineData("perplexitybot")]
    [InlineData("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; Amazonbot/0.1)")]
    public async Task Listed_crawlers_get_403_everywhere_even_with_a_valid_token(string userAgent)
    {
        await using var g = await GatewayHarness.StartAsync();

        var withToken = await g.GetAsync(PoisUrl, TestTokens.Mint(), userAgent);
        var open = await g.GetAsync("/api/platform/v1/config", null, userAgent);

        withToken.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        open.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        GatewayHarness.ProblemType(await withToken.Content.ReadAsStringAsync(Ct)).ShouldBe("https://on.voyage/problems/blocked_client");
        g.Stub.AuthorizationSeen.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("OnVoyage/1.2.0 (Android 14)")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148")]
    [InlineData("")]
    public async Task Real_clients_and_missing_user_agents_are_not_blocked(string userAgent)
    {
        await using var g = await GatewayHarness.StartAsync();

        (await g.GetAsync(PoisUrl, TestTokens.Mint(), userAgent)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_block_list_follows_platform_configuration()
    {
        await using var g = await GatewayHarness.StartAsync("""{"blocked_user_agents":["BadBot"],"edge_config_refresh_seconds":1}""");
        (await g.WaitForSettingsAsync(s => s.BlockedUserAgents.SequenceEqual(["BadBot"]))).ShouldBeTrue("settings were not fetched");

        (await g.GetAsync(PoisUrl, TestTokens.Mint(), "BadBot/2.0")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        // No longer in the list once Platform says so.
        (await g.GetAsync(PoisUrl, TestTokens.Mint(), "GPTBot/1.0")).StatusCode.ShouldBe(HttpStatusCode.OK);

        g.Stub.SecurityJson = """{"blocked_user_agents":["GPTBot"],"edge_config_refresh_seconds":1}""";
        (await g.WaitForSettingsAsync(s => s.BlockedUserAgents.SequenceEqual(["GPTBot"]))).ShouldBeTrue("settings were not refreshed");
        (await g.GetAsync(PoisUrl, TestTokens.Mint(), "GPTBot/1.0")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ---- edge configuration fetch

    [Fact]
    public async Task The_edge_configuration_is_fetched_with_an_internal_token_only()
    {
        await using var g = await GatewayHarness.StartAsync("""{"rate_per_ip_per_min":777}""");
        (await g.WaitForSettingsAsync(s => s.RatePerIpPerMinute == 777)).ShouldBeTrue();

        var header = g.Stub.EdgeCallTokens.First();
        header.ShouldStartWith("Bearer ");
        var payload = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(header["Bearer ".Length..].Split('.')[1]))));
        payload.RootElement.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).ShouldBe(["internal"]);
        payload.RootElement.GetProperty("sub").GetString().ShouldBe("gateway");
    }

    private static string PadBase64(string value) => value.Replace('-', '+').Replace('_', '/').PadRight(value.Length + ((4 - (value.Length % 4)) % 4), '=');

    [Fact]
    public async Task Without_platform_the_annexe_e_defaults_protect_the_gateway_and_stay_in_force_if_it_goes_away()
    {
        await using var g = await GatewayHarness.StartAsync(edgeConfigDown: true);
        await Task.Delay(1500, Ct);

        g.Settings.Current.ShouldBe(EdgeSettings.Defaults);
        (await g.GetAsync(PoisUrl, TestTokens.Mint(), "GPTBot")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        g.Stub.EdgeConfigDown = false;
        g.Stub.SecurityJson = """{"rate_per_ip_per_min":900}""";
        (await g.WaitForSettingsAsync(s => s.RatePerIpPerMinute == 900)).ShouldBeTrue();
        g.Settings.Current.BlockedUserAgents.ShouldBe(EdgeSettings.Defaults.BlockedUserAgents);

        g.Stub.EdgeConfigDown = true;
        await Task.Delay(2500, Ct);
        g.Settings.Current.RatePerIpPerMinute.ShouldBe(900);
    }

    [Fact]
    public async Task Garbage_from_platform_never_weakens_the_settings()
    {
        await using var g = await GatewayHarness.StartAsync("""{"rate_per_ip_per_min":-5,"rate_per_traveler_per_min":"many","blocked_user_agents":"nope"}""");
        await Task.Delay(1800, Ct);

        g.Settings.Current.RatePerIpPerMinute.ShouldBe(EdgeSettings.Defaults.RatePerIpPerMinute);
        g.Settings.Current.RatePerTravelerPerMinute.ShouldBe(EdgeSettings.Defaults.RatePerTravelerPerMinute);
        g.Settings.Current.BlockedUserAgents.ShouldBe(EdgeSettings.Defaults.BlockedUserAgents);
    }

    [Fact]
    public void The_embedded_defaults_equal_the_annexe_e_seed_of_platform()
    {
        using var seed = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "default-config.json")));
        var security = seed.RootElement.GetProperty("security");

        EdgeSettings.Defaults.RatePerTravelerPerMinute.ShouldBe(security.GetProperty("rate_per_traveler_per_min").GetInt32());
        EdgeSettings.Defaults.RatePerIpPerMinute.ShouldBe(security.GetProperty("rate_per_ip_per_min").GetInt32());
        EdgeSettings.Defaults.RefreshSeconds.ShouldBe(security.GetProperty("edge_config_refresh_seconds").GetInt32());
        EdgeSettings.Defaults.BlockedUserAgents.ShouldBe(security.GetProperty("blocked_user_agents").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    // ---- rate limiting (SEC-04)

    [Fact]
    public async Task A_traveler_over_the_limit_gets_429_with_retry_after_while_others_are_unaffected()
    {
        await using var g = await GatewayHarness.StartAsync("""{"rate_per_traveler_per_min":5,"rate_per_ip_per_min":1000}""");
        (await g.WaitForSettingsAsync(s => s.RatePerTravelerPerMinute == 5)).ShouldBeTrue();
        var busy = TestTokens.Mint(Guid.NewGuid());
        var calm = TestTokens.Mint(Guid.NewGuid());

        for (var i = 0; i < 5; i++)
        {
            (await g.GetAsync(PoisUrl, busy)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var limited = await g.GetAsync(PoisUrl, busy);
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBeGreaterThan(0);
        GatewayHarness.ProblemType(await limited.Content.ReadAsStringAsync(Ct)).ShouldBe("https://on.voyage/problems/rate_limited");

        (await g.GetAsync(PoisUrl, calm)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_client_address_over_the_limit_is_throttled_even_without_a_token()
    {
        await using var g = await GatewayHarness.StartAsync("""{"rate_per_traveler_per_min":1000,"rate_per_ip_per_min":4}""");
        (await g.WaitForSettingsAsync(s => s.RatePerIpPerMinute == 4)).ShouldBeTrue();

        for (var i = 0; i < 4; i++)
        {
            (await g.GetAsync("/api/platform/v1/config")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await g.GetAsync("/api/platform/v1/config")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        // Rejected before authentication, so a flood of bad tokens cannot be used to probe for free.
        (await g.GetAsync(PoisUrl, "garbage")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Health_checks_are_never_throttled()
    {
        await using var g = await GatewayHarness.StartAsync("""{"rate_per_ip_per_min":2,"rate_per_traveler_per_min":2}""");
        (await g.WaitForSettingsAsync(s => s.RatePerIpPerMinute == 2)).ShouldBeTrue();

        for (var i = 0; i < 10; i++)
        {
            (await g.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_lowered_limit_applies_to_requests_after_the_refresh()
    {
        await using var g = await GatewayHarness.StartAsync("""{"rate_per_traveler_per_min":100}""");
        var token = TestTokens.Mint(Guid.NewGuid());
        (await g.GetAsync(PoisUrl, token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        g.Stub.SecurityJson = """{"rate_per_traveler_per_min":1}""";
        (await g.WaitForSettingsAsync(s => s.RatePerTravelerPerMinute == 1)).ShouldBeTrue();

        (await g.GetAsync(PoisUrl, token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await g.GetAsync(PoisUrl, token)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    // ---- privacy: positions never reach traces or logs (§17.1)

    [Fact]
    public async Task A_position_in_the_query_string_appears_in_no_trace_and_no_log()
    {
        await using var g = await GatewayHarness.StartAsync();
        var token = TestTokens.Mint();

        var response = await g.GetAsync($"{PoisUrl}?lat=43.29517&lon=5.37411&radius=1500", token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var unauthorized = await g.GetAsync($"{PoisUrl}?lat=43.29517&lon=5.37411", null);
        unauthorized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await Task.Delay(300, Ct);

        var observed = g.Telemetry.Observed.ToArray();
        observed.Length.ShouldBeGreaterThan(10, "the capture saw almost nothing, so this test would pass for the wrong reason");
        observed.Any(line => line.Contains("Microsoft.AspNetCore.Hosting.HttpRequestIn", StringComparison.Ordinal) || line.StartsWith("tag:url.path", StringComparison.Ordinal))
            .ShouldBeTrue("no request span was captured");

        observed.Where(line => line.Contains("43.29517", StringComparison.Ordinal) || line.Contains("5.37411", StringComparison.Ordinal)).ShouldBeEmpty();
        observed.Where(line => line.StartsWith("tag:url.query=", StringComparison.Ordinal) && line.Length > "tag:url.query=".Length).ShouldBeEmpty();
    }
}
