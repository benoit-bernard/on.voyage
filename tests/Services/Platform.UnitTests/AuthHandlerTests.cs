using OnVoyage.Platform.Application.Features.Auth;
using OnVoyage.Platform.Contracts;

namespace Platform.UnitTests;

public sealed class AuthHandlerTests
{
    private readonly AuthWorld _w = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<OnVoyage.Platform.Application.Result<AuthSessionDto>> Anonymous() =>
        StartAnonymousSessionHandler.Handle(new StartAnonymousSessionCommand(), _w.AccountStore, _w.RefreshStore, _w.Tokens, _w.Credentials, _w.SettingsProvider, _w.Clock, Ct);

    private Task<OnVoyage.Platform.Application.Result<bool>> Request(string email) =>
        RequestOtpHandler.Handle(new RequestOtpCommand(email), _w.OtpStore, _w.Credentials, _w.Email, _w.SettingsProvider, _w.Clock, Ct);

    private Task<OnVoyage.Platform.Application.Result<AuthSessionDto>> Verify(Guid? caller, string email, string code) =>
        VerifyOtpHandler.Handle(new VerifyOtpCommand(caller, email, code), _w.OtpStore, _w.AccountStore, _w.RefreshStore, _w.Tokens, _w.Credentials, _w.SettingsProvider, _w.Clock, Ct);

    private Task<OnVoyage.Platform.Application.Result<AuthSessionDto>> Refresh(string token) =>
        RefreshSessionHandler.Handle(new RefreshSessionCommand(token), _w.AccountStore, _w.RefreshStore, _w.Tokens, _w.Credentials, _w.SettingsProvider, _w.Clock, Ct);

    // ---- anonymous session

    [Fact]
    public async Task First_launch_creates_an_anonymous_account_and_stores_only_a_hash_of_the_refresh_token()
    {
        var session = (await Anonymous()).Value!;

        session.IsAnonymous.ShouldBeTrue();
        session.Email.ShouldBeNull();
        _w.Accounts[session.TravelerId].IsAnonymous.ShouldBeTrue();
        _w.Refresh.ShouldHaveSingleItem().TokenHash.ShouldNotContain(session.RefreshToken);
        (session.AccessTokenExpiresAt - AuthWorld.Start).ShouldBe(TimeSpan.FromHours(1));
        (session.RefreshTokenExpiresAt - AuthWorld.Start).ShouldBe(TimeSpan.FromDays(90));
    }

    // ---- request

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    [InlineData("two@words@example.org")]
    public async Task Invalid_addresses_are_rejected_before_anything_is_sent(string email)
    {
        (await Request(email)).Error!.Code.ShouldBe("validation");
        _w.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_code_is_six_digits_hashed_at_rest_and_the_address_is_normalised()
    {
        (await Request("  Claire@Example.ORG ")).IsSuccess.ShouldBeTrue();

        _w.Sent.ShouldHaveSingleItem().Email.ShouldBe("claire@example.org");
        _w.LastCode.Length.ShouldBe(6);
        _w.Challenges.Single().CodeHash.ShouldNotContain(_w.LastCode);
        (_w.Challenges.Single().ExpiresAt - AuthWorld.Start).ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task A_second_request_within_the_cooldown_is_throttled_with_the_wait_time()
    {
        await Request("a@example.org");
        _w.Clock.Advance(TimeSpan.FromSeconds(20));

        var second = await Request("a@example.org");

        second.Error!.Code.ShouldBe("otp_cooldown");
        second.Error.RetryAfterSeconds.ShouldBe(40);
        _w.Sent.Count.ShouldBe(1);

        _w.Clock.Advance(TimeSpan.FromSeconds(41));
        (await Request("a@example.org")).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task At_most_five_codes_per_address_per_hour()
    {
        for (var i = 0; i < 5; i++)
        {
            (await Request("a@example.org")).IsSuccess.ShouldBeTrue();
            _w.Clock.Advance(TimeSpan.FromSeconds(61));
        }

        (await Request("a@example.org")).Error!.Code.ShouldBe("otp_rate_limited");
        (await Request("other@example.org")).IsSuccess.ShouldBeTrue();

        _w.Clock.Advance(TimeSpan.FromHours(1));
        (await Request("a@example.org")).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_new_code_invalidates_the_previous_one()
    {
        await Request("a@example.org");
        var first = _w.LastCode;
        _w.Clock.Advance(TimeSpan.FromSeconds(61));
        await Request("a@example.org");

        (await Verify(null, "a@example.org", first)).Error!.Code.ShouldBe("otp_invalid");
        (await Verify(null, "a@example.org", _w.LastCode)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task When_the_provider_fails_the_traveler_is_told_and_the_failed_code_is_dead()
    {
        _w.EmailFails = true;

        (await Request("a@example.org")).Error!.Code.ShouldBe("email_unavailable");

        _w.EmailFails = false;
        _w.Challenges.ShouldAllBe(challenge => challenge.ConsumedAt != null);
        (await Verify(null, "a@example.org", "100001")).Error!.Code.ShouldBe("otp_invalid");
    }

    // ---- verify

    [Fact]
    public async Task Verifying_links_the_email_and_keeps_the_traveler_id()
    {
        var anonymous = (await Anonymous()).Value!;
        await Request("claire@example.org");

        var linked = (await Verify(anonymous.TravelerId, "claire@example.org", _w.LastCode)).Value!;

        linked.TravelerId.ShouldBe(anonymous.TravelerId);
        linked.IsAnonymous.ShouldBeFalse();
        linked.Email.ShouldBe("claire@example.org");
        _w.Accounts.Count.ShouldBe(1);
        _w.Accounts[anonymous.TravelerId].EmailVerifiedAt.ShouldBe(AuthWorld.Start);
    }

    [Fact]
    public async Task An_expired_code_is_refused_with_a_clear_reason_and_cannot_be_reused()
    {
        var anonymous = (await Anonymous()).Value!;
        await Request("claire@example.org");
        var code = _w.LastCode;
        _w.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        (await Verify(anonymous.TravelerId, "claire@example.org", code)).Error!.Code.ShouldBe("otp_expired");
        (await Verify(anonymous.TravelerId, "claire@example.org", code)).Error!.Code.ShouldBe("otp_invalid");
        _w.Accounts[anonymous.TravelerId].IsAnonymous.ShouldBeTrue();
    }

    [Fact]
    public async Task A_code_is_valid_until_the_last_second_of_its_lifetime()
    {
        await Request("claire@example.org");
        _w.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));

        (await Verify(null, "claire@example.org", _w.LastCode)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_challenge_even_if_the_sixth_is_right()
    {
        await Request("claire@example.org");
        var good = _w.LastCode;

        for (var i = 1; i <= 4; i++)
        {
            (await Verify(null, "claire@example.org", "000000")).Error!.Code.ShouldBe("otp_invalid");
        }

        (await Verify(null, "claire@example.org", "000000")).Error!.Code.ShouldBe("otp_locked");
        (await Verify(null, "claire@example.org", good)).Error!.Code.ShouldBe("otp_invalid");
    }

    [Fact]
    public async Task A_correct_code_cannot_be_replayed()
    {
        await Request("claire@example.org");
        var code = _w.LastCode;
        await Verify(null, "claire@example.org", code);

        (await Verify(null, "claire@example.org", code)).Error!.Code.ShouldBe("otp_invalid");
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("")]
    public async Task Malformed_codes_never_reach_the_comparison(string code)
    {
        await Request("claire@example.org");

        (await Verify(null, "claire@example.org", code)).Error!.Code.ShouldBe("validation");
        _w.Challenges.Single().Attempts.ShouldBe(0);
    }

    [Fact]
    public async Task A_code_for_another_address_is_not_valid()
    {
        await Request("claire@example.org");

        (await Verify(null, "mallory@example.org", _w.LastCode)).Error!.Code.ShouldBe("otp_invalid");
    }

    [Fact]
    public async Task Signing_in_on_a_new_device_returns_the_existing_account_and_retires_the_new_anonymous_one()
    {
        var phone = (await Anonymous()).Value!;
        await Request("claire@example.org");
        await Verify(phone.TravelerId, "claire@example.org", _w.LastCode);

        // Reinstall: a fresh anonymous session, then the same address.
        var fresh = (await Anonymous()).Value!;
        _w.Clock.Advance(TimeSpan.FromMinutes(2));
        await Request("claire@example.org");
        var restored = (await Verify(fresh.TravelerId, "claire@example.org", _w.LastCode)).Value!;

        restored.TravelerId.ShouldBe(phone.TravelerId);
        restored.IsAnonymous.ShouldBeFalse();
        _w.Accounts[fresh.TravelerId].ReplacedBy.ShouldBe(phone.TravelerId);
        _w.Refresh.Where(r => r.AccountId == fresh.TravelerId).ShouldAllBe(r => r.RevokedAt != null);
        (await Refresh(fresh.RefreshToken)).Error!.Code.ShouldBe("invalid_refresh_token");
    }

    [Fact]
    public async Task Verifying_without_any_session_creates_the_account()
    {
        await Request("new@example.org");

        var session = (await Verify(null, "new@example.org", _w.LastCode)).Value!;

        session.IsAnonymous.ShouldBeFalse();
        _w.Accounts[session.TravelerId].Email.ShouldBe("new@example.org");
    }

    [Fact]
    public async Task A_verified_account_cannot_attach_a_second_address_or_sign_in_to_another_account()
    {
        var a = (await Anonymous()).Value!;
        await Request("a@example.org");
        await Verify(a.TravelerId, "a@example.org", _w.LastCode);

        var b = (await Anonymous()).Value!;
        await Request("b@example.org");
        await Verify(b.TravelerId, "b@example.org", _w.LastCode);

        _w.Clock.Advance(TimeSpan.FromMinutes(2));
        await Request("c@example.org");
        (await Verify(a.TravelerId, "c@example.org", _w.LastCode)).Error!.Code.ShouldBe("email_already_linked");

        _w.Clock.Advance(TimeSpan.FromMinutes(2));
        await Request("b@example.org");
        (await Verify(a.TravelerId, "b@example.org", _w.LastCode)).Error!.Code.ShouldBe("email_already_linked");
    }

    [Fact]
    public async Task Bootstrap_admin_addresses_receive_the_admin_role_only_after_verification()
    {
        _w.Settings = _w.Settings with { BootstrapAdminEmails = ["boss@example.org"] };
        var anonymous = (await Anonymous()).Value!;
        anonymous.Roles.ShouldBeEmpty();
        await Request("boss@example.org");

        var session = (await Verify(anonymous.TravelerId, "boss@example.org", _w.LastCode)).Value!;

        session.Roles.ShouldBe(["admin"]);

        _w.Clock.Advance(TimeSpan.FromMinutes(2));
        await Request("other@example.org");
        (await Verify(null, "other@example.org", _w.LastCode)).Value!.Roles.ShouldBeEmpty();
    }

    // ---- refresh

    [Fact]
    public async Task Refreshing_rotates_the_token_and_keeps_the_account()
    {
        var first = (await Anonymous()).Value!;
        _w.Clock.Advance(TimeSpan.FromMinutes(30));

        var second = (await Refresh(first.RefreshToken)).Value!;

        second.TravelerId.ShouldBe(first.TravelerId);
        second.RefreshToken.ShouldNotBe(first.RefreshToken);
        _w.Accounts[first.TravelerId].LastActiveAt.ShouldBe(_w.Clock.GetUtcNow());
        (await Refresh(second.RefreshToken)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Replaying_a_used_refresh_token_revokes_the_whole_family()
    {
        var first = (await Anonymous()).Value!;
        var second = (await Refresh(first.RefreshToken)).Value!;

        (await Refresh(first.RefreshToken)).Error!.Code.ShouldBe("invalid_refresh_token");

        // The legitimate holder of the newest token is cut off too: a stolen token was in play.
        (await Refresh(second.RefreshToken)).Error!.Code.ShouldBe("invalid_refresh_token");
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown-token")]
    public async Task Unknown_refresh_tokens_are_refused(string token) =>
        (await Refresh(token)).Error!.Code.ShouldBe("invalid_refresh_token");

    [Fact]
    public async Task Expired_refresh_tokens_are_refused()
    {
        var session = (await Anonymous()).Value!;
        _w.Clock.Advance(TimeSpan.FromDays(91));

        (await Refresh(session.RefreshToken)).Error!.Code.ShouldBe("invalid_refresh_token");
    }

    [Fact]
    public async Task Signing_out_revokes_the_family_and_is_idempotent()
    {
        var session = (await Anonymous()).Value!;

        for (var i = 0; i < 2; i++)
        {
            (await SignOutHandler.Handle(new SignOutCommand(session.RefreshToken), _w.RefreshStore, _w.Credentials, _w.Clock, Ct)).IsSuccess.ShouldBeTrue();
        }

        (await Refresh(session.RefreshToken)).Error!.Code.ShouldBe("invalid_refresh_token");
    }

    [Fact]
    public async Task Account_lookup_hides_retired_accounts()
    {
        var fresh = (await Anonymous()).Value!;
        (await GetAccountHandler.Handle(new GetAccountQuery(fresh.TravelerId), _w.AccountStore, Ct)).IsSuccess.ShouldBeTrue();

        _w.Accounts[fresh.TravelerId] = _w.Accounts[fresh.TravelerId] with { ReplacedBy = Guid.NewGuid() };

        (await GetAccountHandler.Handle(new GetAccountQuery(fresh.TravelerId), _w.AccountStore, Ct)).Error!.Code.ShouldBe("account_not_found");
        (await GetAccountHandler.Handle(new GetAccountQuery(Guid.NewGuid()), _w.AccountStore, Ct)).Error!.Code.ShouldBe("account_not_found");
    }
}
