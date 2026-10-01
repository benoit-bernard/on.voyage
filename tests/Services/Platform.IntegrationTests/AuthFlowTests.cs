using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using OnVoyage.Platform.Contracts;
using OnVoyage.TestInfrastructure;

namespace Platform.IntegrationTests;

/// <summary>F-01 end to end: anonymous session, e-mail code, linking, restore, refresh. Real database and real JWT validation.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AuthFlowTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlatformHarness _p = null!;

    public async ValueTask InitializeAsync() => _p = await PlatformHarness.StartAsync(postgres);

    public async ValueTask DisposeAsync() => await _p.DisposeAsync();

    private static async Task<string> ProblemTypeAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("type").GetString()!;

    [Fact]
    public async Task First_launch_gives_a_working_anonymous_session()
    {
        var session = await _p.AnonymousAsync();

        session.IsAnonymous.ShouldBeTrue();
        session.AccessTokenExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(50));
        var me = await (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/me", session)).Content.ReadFromJsonAsync<AccountDto>(Ct);
        me!.TravelerId.ShouldBe(session.TravelerId);
        me.IsAnonymous.ShouldBeTrue();
        me.Email.ShouldBeNull();
    }

    [Fact]
    public async Task Linking_an_email_keeps_the_traveler_id_and_upgrades_the_token()
    {
        var anonymous = await _p.AnonymousAsync();
        (await _p.RequestCodeAsync("claire@example.org", anonymous)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var verified = await _p.VerifyAsync("claire@example.org", _p.Email.LastCodeFor("claire@example.org"), anonymous);
        verified.StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = (await verified.Content.ReadFromJsonAsync<AuthSessionDto>(Ct))!;

        session.TravelerId.ShouldBe(anonymous.TravelerId);
        session.IsAnonymous.ShouldBeFalse();
        session.Email.ShouldBe("claire@example.org");

        var me = await (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/me", session)).Content.ReadFromJsonAsync<AccountDto>(Ct);
        me!.IsAnonymous.ShouldBeFalse();
        me.TravelerId.ShouldBe(anonymous.TravelerId);

        // Consents given while anonymous are still there after linking: same traveler id.
        (await _p.SendAsync(HttpMethod.Put, "/api/platform/v1/me/consents/analytics", anonymous, new { granted = true, textVersion = "v1" })).EnsureSuccessStatusCode();
        var consents = await (await _p.SendAsync(HttpMethod.Get, "/api/platform/v1/me/consents", session)).Content.ReadFromJsonAsync<List<ConsentDto>>(Ct);
        consents!.Single(c => c.Kind == "analytics").Granted.ShouldBeTrue();
    }

    [Fact]
    public async Task The_code_never_appears_in_the_database_only_a_hash()
    {
        await _p.RequestCodeAsync("claire@example.org");
        var code = _p.Email.LastCodeFor("claire@example.org");

        await using var connection = new NpgsqlConnection(_p.Connection);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select code_hash, email from platform.otp_challenge", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        reader.GetString(0).ShouldNotContain(code);
        reader.GetString(0).Length.ShouldBe(64);
    }

    [Fact]
    public async Task Sign_in_on_a_new_device_restores_the_original_account()
    {
        var phone = await _p.SignInAsync("claire@example.org", await _p.AnonymousAsync());

        var reinstall = await _p.AnonymousAsync();
        reinstall.TravelerId.ShouldNotBe(phone.TravelerId);
        var restored = await _p.SignInAsync("claire@example.org", reinstall);

        restored.TravelerId.ShouldBe(phone.TravelerId);
        // The abandoned anonymous session can no longer be refreshed.
        (await _p.SendAsync(HttpMethod.Post, "/api/platform/v1/auth/refresh", null, new { refreshToken = reinstall.RefreshToken })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_expired_code_says_so_and_the_traveler_can_ask_for_another()
    {
        var anonymous = await _p.AnonymousAsync();
        await _p.RequestCodeAsync("claire@example.org", anonymous);
        var stale = _p.Email.LastCodeFor("claire@example.org");
        _p.Clock.Advance(TimeSpan.FromMinutes(11));

        var expired = await _p.VerifyAsync("claire@example.org", stale, anonymous);
        expired.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemTypeAsync(expired)).ShouldBe("https://on.voyage/problems/otp_expired");

        (await _p.RequestCodeAsync("claire@example.org", anonymous)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await _p.VerifyAsync("claire@example.org", _p.Email.LastCodeFor("claire@example.org"), anonymous)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Wrong_codes_are_refused_and_the_fifth_locks_the_challenge()
    {
        await _p.RequestCodeAsync("claire@example.org");
        var good = _p.Email.LastCodeFor("claire@example.org");
        var wrong = good == "000000" ? "111111" : "000000";

        for (var i = 0; i < 4; i++)
        {
            var attempt = await _p.VerifyAsync("claire@example.org", wrong);
            (await ProblemTypeAsync(attempt)).ShouldBe("https://on.voyage/problems/otp_invalid");
        }

        (await ProblemTypeAsync(await _p.VerifyAsync("claire@example.org", wrong))).ShouldBe("https://on.voyage/problems/otp_locked");
        (await _p.VerifyAsync("claire@example.org", good)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Asking_again_too_soon_is_429_with_retry_after_and_an_hourly_cap_applies()
    {
        await _p.RequestCodeAsync("claire@example.org");
        var tooSoon = await _p.RequestCodeAsync("claire@example.org");

        tooSoon.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        tooSoon.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBeInRange(1, 60);
        (await ProblemTypeAsync(tooSoon)).ShouldBe("https://on.voyage/problems/otp_cooldown");

        for (var i = 0; i < 4; i++)
        {
            _p.Clock.Advance(TimeSpan.FromSeconds(61));
            (await _p.RequestCodeAsync("claire@example.org")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        _p.Clock.Advance(TimeSpan.FromSeconds(61));
        var capped = await _p.RequestCodeAsync("claire@example.org");
        capped.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ProblemTypeAsync(capped)).ShouldBe("https://on.voyage/problems/otp_rate_limited");
        (await _p.RequestCodeAsync("someone.else@example.org")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task The_answer_does_not_reveal_whether_an_address_has_an_account()
    {
        await _p.SignInAsync("known@example.org", await _p.AnonymousAsync());
        _p.Clock.Advance(TimeSpan.FromMinutes(2));

        var known = await _p.RequestCodeAsync("known@example.org");
        var unknown = await _p.RequestCodeAsync("unknown@example.org");

        known.StatusCode.ShouldBe(unknown.StatusCode);
        (await known.Content.ReadAsStringAsync(Ct)).ShouldBe(await unknown.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_mail_provider_outage_is_a_502_and_does_not_burn_the_cooldown()
    {
        _p.Email.Fail = true;
        var failed = await _p.RequestCodeAsync("claire@example.org");
        failed.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await ProblemTypeAsync(failed)).ShouldBe("https://on.voyage/problems/email_unavailable");

        _p.Email.Fail = false;
        _p.Clock.Advance(TimeSpan.FromSeconds(61));
        (await _p.RequestCodeAsync("claire@example.org")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    public async Task Invalid_addresses_are_400(string email) =>
        (await _p.RequestCodeAsync(email)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Refresh_rotates_tokens_and_a_replay_kills_the_family()
    {
        var first = await _p.AnonymousAsync();
        var refreshed = await _p.SendAsync(HttpMethod.Post, "/api/platform/v1/auth/refresh", null, new { refreshToken = first.RefreshToken });
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = (await refreshed.Content.ReadFromJsonAsync<AuthSessionDto>(Ct))!;
        second.TravelerId.ShouldBe(first.TravelerId);
        second.RefreshToken.ShouldNotBe(first.RefreshToken);

        (await _p.SendAsync(HttpMethod.Post, "/api/platform/v1/auth/refresh", null, new { refreshToken = first.RefreshToken })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _p.SendAsync(HttpMethod.Post, "/api/platform/v1/auth/refresh", null, new { refreshToken = second.RefreshToken })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Concurrent_refreshes_of_one_token_let_exactly_one_through()
    {
        var session = await _p.AnonymousAsync();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            _p.SendAsync(HttpMethod.Post, "/api/platform/v1/auth/refresh", null, new { refreshToken = session.RefreshToken })));

        attempts.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        attempts.Count(response => response.StatusCode == HttpStatusCode.Unauthorized).ShouldBe(5);
    }

    [Fact]
    public async Task Sign_out_revokes_the_refresh_token()
    {
        var session = await _p.AnonymousAsync();

        (await _p.SendAsync(HttpMethod.Post, "/api/platform/v1/auth/signout", null, new { refreshToken = session.RefreshToken })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _p.SendAsync(HttpMethod.Post, "/api/platform/v1/auth/refresh", null, new { refreshToken = session.RefreshToken })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_verified_account_cannot_change_its_address_here()
    {
        var verified = await _p.SignInAsync("claire@example.org", await _p.AnonymousAsync());

        await _p.RequestCodeAsync("other@example.org", verified);
        var attempt = await _p.VerifyAsync("other@example.org", _p.Email.LastCodeFor("other@example.org"), verified);

        attempt.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemTypeAsync(attempt)).ShouldBe("https://on.voyage/problems/email_already_linked");
    }
}
