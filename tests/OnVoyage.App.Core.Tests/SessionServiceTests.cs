using Microsoft.Extensions.Time.Testing;
using OnVoyage.App.Core.Auth;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.App.Core.Tests;

public sealed class SessionServiceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ScriptedClient(FakeTimeProvider clock) : IAuthClient
    {
        private int _issued;

        public int Anonymous { get; private set; }
        public int Refreshes { get; private set; }
        public int SignOuts { get; private set; }
        public AuthFailure? RefreshFailure { get; set; }
        public AuthFailure? VerifyFailure { get; set; }
        public Guid NextVerifiedTravelerId { get; set; } = Guid.Empty;
        public List<string?> TokensSeen { get; } = [];

        public AuthSessionDto Make(Guid? id = null, bool anonymous = true, string? email = null) =>
            new($"access-{++_issued}", clock.GetUtcNow().AddHours(1), $"refresh-{_issued}", clock.GetUtcNow().AddDays(90), id ?? Guid.NewGuid(), anonymous, email, []);

        public Task<AuthResult<AuthSessionDto>> StartAnonymousAsync(CancellationToken cancellationToken)
        {
            Anonymous++;
            return Task.FromResult(AuthResult.Success(Make()));
        }

        public Task<AuthResult<AuthSessionDto>> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            Refreshes++;
            return Task.FromResult(RefreshFailure is null ? AuthResult.Success(Make(Current?.TravelerId)) : AuthResult.Fail<AuthSessionDto>(RefreshFailure));
        }

        public AuthSessionDto? Current { get; set; }

        public Task<AuthResult<bool>> RequestCodeAsync(string email, string? accessToken, CancellationToken cancellationToken)
        {
            TokensSeen.Add(accessToken);
            return Task.FromResult(AuthResult.Success(true));
        }

        public Task<AuthResult<AuthSessionDto>> VerifyCodeAsync(string email, string code, string? accessToken, CancellationToken cancellationToken)
        {
            TokensSeen.Add(accessToken);
            return Task.FromResult(VerifyFailure is null
                ? AuthResult.Success(Make(NextVerifiedTravelerId == Guid.Empty ? Current?.TravelerId : NextVerifiedTravelerId, false, email))
                : AuthResult.Fail<AuthSessionDto>(VerifyFailure));
        }

        public Task SignOutAsync(string refreshToken, CancellationToken cancellationToken)
        {
            SignOuts++;
            return Task.CompletedTask;
        }
    }

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
    private readonly InMemorySessionStore _store = new();
    private readonly ScriptedClient _client;
    private readonly SessionService _service;

    public SessionServiceTests()
    {
        _client = new ScriptedClient(_clock);
        _service = new SessionService(_client, _store, _clock);
    }

    public void Dispose() => _service.Dispose();

    [Fact]
    public async Task First_use_creates_an_anonymous_session_once_and_stores_it()
    {
        var first = await _service.EnsureSessionAsync(Ct);
        var second = await _service.EnsureSessionAsync(Ct);

        second.ShouldBe(first);
        _client.Anonymous.ShouldBe(1);
        (await _store.LoadAsync(Ct)).ShouldBe(first);
    }

    [Fact]
    public async Task A_stored_session_is_reused_after_restart_without_any_call()
    {
        var existing = _client.Make();
        await _store.SaveAsync(existing, Ct);

        (await _service.EnsureSessionAsync(Ct)).ShouldBe(existing);
        _client.Anonymous.ShouldBe(0);
        _client.Refreshes.ShouldBe(0);
    }

    [Fact]
    public async Task The_token_is_refreshed_once_it_is_within_two_minutes_of_expiry()
    {
        var first = await _service.EnsureSessionAsync(Ct);
        _client.Current = first;

        _clock.Advance(TimeSpan.FromMinutes(57));
        (await _service.EnsureSessionAsync(Ct)).AccessToken.ShouldBe(first.AccessToken);
        _clock.Advance(TimeSpan.FromMinutes(2));
        var refreshed = await _service.EnsureSessionAsync(Ct);

        refreshed.AccessToken.ShouldNotBe(first.AccessToken);
        refreshed.TravelerId.ShouldBe(first.TravelerId);
        _client.Refreshes.ShouldBe(1);
        (await _store.LoadAsync(Ct)).ShouldBe(refreshed);
    }

    [Fact]
    public async Task Parallel_callers_trigger_a_single_refresh()
    {
        var first = await _service.EnsureSessionAsync(Ct);
        _client.Current = first;
        _clock.Advance(TimeSpan.FromMinutes(59));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _service.GetAccessTokenAsync(Ct)));

        _client.Refreshes.ShouldBe(1);
        tokens.Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_rejected_refresh_token_falls_back_to_a_new_anonymous_session()
    {
        var first = await _service.EnsureSessionAsync(Ct);
        _client.RefreshFailure = new AuthFailure(AuthFailure.InvalidRefreshToken, "revoked");
        _clock.Advance(TimeSpan.FromMinutes(59));

        var next = await _service.EnsureSessionAsync(Ct);

        next.TravelerId.ShouldNotBe(first.TravelerId);
        next.IsAnonymous.ShouldBeTrue();
        _client.Anonymous.ShouldBe(2);
    }

    [Fact]
    public async Task A_network_failure_keeps_a_still_valid_access_token_and_throws_once_it_is_gone()
    {
        var first = await _service.EnsureSessionAsync(Ct);
        _client.RefreshFailure = new AuthFailure(AuthFailure.Network, "offline");

        _clock.Advance(TimeSpan.FromMinutes(59));
        (await _service.EnsureSessionAsync(Ct)).AccessToken.ShouldBe(first.AccessToken);

        _clock.Advance(TimeSpan.FromMinutes(5));
        await Should.ThrowAsync<HttpRequestException>(() => _service.EnsureSessionAsync(Ct));
    }

    [Fact]
    public async Task Invalidating_forces_a_refresh_even_when_the_token_looks_fresh()
    {
        var first = await _service.EnsureSessionAsync(Ct);
        _client.Current = first;

        _service.InvalidateAccessToken();
        var refreshed = await _service.EnsureSessionAsync(Ct);

        refreshed.AccessToken.ShouldNotBe(first.AccessToken);
        (await _service.EnsureSessionAsync(Ct)).ShouldBe(refreshed);
        _client.Refreshes.ShouldBe(1);
    }

    [Fact]
    public async Task Linking_an_email_sends_the_anonymous_token_and_keeps_the_traveler_id()
    {
        var anonymous = await _service.EnsureSessionAsync(Ct);
        _client.Current = anonymous;

        (await _service.RequestCodeAsync("claire@example.org", Ct)).IsSuccess.ShouldBeTrue();
        var result = await _service.VerifyCodeAsync("claire@example.org", "123456", Ct);

        result.Value!.TravelerId.ShouldBe(anonymous.TravelerId);
        result.Value.IsAnonymous.ShouldBeFalse();
        _client.TokensSeen.ShouldAllBe(token => token == anonymous.AccessToken);
        (await _store.LoadAsync(Ct))!.Email.ShouldBe("claire@example.org");
        (await _service.EnsureSessionAsync(Ct)).IsAnonymous.ShouldBeFalse();
    }

    [Fact]
    public async Task Restoring_an_account_replaces_the_device_session_with_the_existing_traveler_id()
    {
        var anonymous = await _service.EnsureSessionAsync(Ct);
        _client.Current = anonymous;
        var original = Guid.CreateVersion7();
        _client.NextVerifiedTravelerId = original;

        var restored = (await _service.VerifyCodeAsync("claire@example.org", "123456", Ct)).Value!;

        restored.TravelerId.ShouldBe(original);
        (await _service.EnsureSessionAsync(Ct)).TravelerId.ShouldBe(original);
    }

    [Fact]
    public async Task A_wrong_code_leaves_the_session_untouched_and_reports_the_reason()
    {
        var anonymous = await _service.EnsureSessionAsync(Ct);
        _client.VerifyFailure = new AuthFailure("otp_expired", "expired");

        var result = await _service.VerifyCodeAsync("claire@example.org", "123456", Ct);

        result.Failure!.Code.ShouldBe("otp_expired");
        (await _service.EnsureSessionAsync(Ct)).ShouldBe(anonymous);
    }

    [Fact]
    public async Task Sign_out_revokes_on_the_server_and_starts_a_fresh_anonymous_session()
    {
        var verified = _client.Make(anonymous: false, email: "claire@example.org");
        await _store.SaveAsync(verified, Ct);

        await _service.SignOutAsync(Ct);

        _client.SignOuts.ShouldBe(1);
        var next = await _service.EnsureSessionAsync(Ct);
        next.TravelerId.ShouldNotBe(verified.TravelerId);
        next.IsAnonymous.ShouldBeTrue();
    }

    [Fact]
    public async Task Session_changes_are_announced()
    {
        var seen = new List<Guid>();
        _service.SessionChanged += session => seen.Add(session.TravelerId);

        var created = await _service.EnsureSessionAsync(Ct);

        seen.ShouldBe([created.TravelerId]);
    }
}
