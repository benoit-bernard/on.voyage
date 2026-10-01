using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OnVoyage.App.Core.Auth;
using OnVoyage.Platform.Contracts;
using OnVoyage.UI.Components.Pages;

namespace OnVoyage.UI.Components.Tests;

public sealed class AccountPageTests : BunitContext
{
    private readonly IAuthClient _client = Substitute.For<IAuthClient>();
    private readonly Guid _traveler = Guid.CreateVersion7();

    private AuthSessionDto Session(bool anonymous, string? email = null) =>
        new("access", DateTimeOffset.UtcNow.AddHours(1), "refresh", DateTimeOffset.UtcNow.AddDays(90), _traveler, anonymous, email, []);

    private void Arrange()
    {
        _client.StartAnonymousAsync(Arg.Any<CancellationToken>()).Returns(AuthResult.Success(Session(true)));
        _client.RequestCodeAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(AuthResult.Success(true));
        Services.AddSingleton(_client);
        Services.AddSingleton<ISessionStore, InMemorySessionStore>();
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<SessionService>();
    }

    private IRenderedComponent<Account> Open()
    {
        Arrange();
        var cut = Render<Account>();
        cut.WaitForState(() => cut.FindAll("#email").Count == 1);
        return cut;
    }

    private static void AskForCode(IRenderedComponent<Account> cut, string email = "claire@example.org")
    {
        cut.Find("#email").Input(email);
        cut.Find("form").Submit();
        cut.WaitForState(() => cut.FindAll("#code").Count == 1);
    }

    [Fact]
    public void An_anonymous_traveler_is_offered_the_email_form_without_any_password_field()
    {
        var cut = Open();

        cut.Markup.ShouldContain("sans compte");
        cut.FindAll("input[type=password]").ShouldBeEmpty();
        cut.Find("#email").GetAttribute("autocomplete").ShouldBe("email");
    }

    [Fact]
    public void Asking_for_a_code_moves_to_the_code_step_and_sends_the_trimmed_address()
    {
        var cut = Open();

        AskForCode(cut, "  claire@example.org ");

        cut.Markup.ShouldContain("claire@example.org");
        cut.Find("#code").GetAttribute("autocomplete").ShouldBe("one-time-code");
        _client.Received(1).RequestCodeAsync("claire@example.org", "access", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_wrong_code_shows_a_clear_message_and_keeps_the_form()
    {
        _client.VerifyCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(AuthResult.Fail<AuthSessionDto>(new AuthFailure("otp_invalid", "invalid")));
        var cut = Open();
        AskForCode(cut);

        cut.Find("#code").Input("000000");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.ShouldContain("n'est pas valide"));
        cut.FindAll("#code").Count.ShouldBe(1);
    }

    [Fact]
    public void An_expired_code_clears_the_field_and_points_to_resending()
    {
        _client.VerifyCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(AuthResult.Fail<AuthSessionDto>(new AuthFailure("otp_expired", "expired")));
        var cut = Open();
        AskForCode(cut);

        cut.Find("#code").Input("123456");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.ShouldContain("a expiré"));
        cut.Find("#code").GetAttribute("value").ShouldBeNullOrEmpty();
        cut.Markup.ShouldContain("Renvoyer un code");
    }

    [Fact]
    public void The_cooldown_message_tells_how_long_to_wait()
    {
        var cut = Open();
        _client.RequestCodeAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(AuthResult.Fail<bool>(new AuthFailure("otp_cooldown", "wait", 37)));

        cut.Find("#email").Input("claire@example.org");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.ShouldContain("37 secondes"));
        cut.FindAll("#code").ShouldBeEmpty();
    }

    [Fact]
    public void A_valid_code_shows_the_connected_account()
    {
        _client.VerifyCodeAsync("claire@example.org", "123456", "access", Arg.Any<CancellationToken>())
            .Returns(AuthResult.Success(Session(false, "claire@example.org")));
        var cut = Open();
        AskForCode(cut);

        cut.Find("#code").Input("123456");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Connecté avec"));
        cut.Markup.ShouldContain("claire@example.org");
        cut.Markup.ShouldContain("Se déconnecter");
    }

    [Fact]
    public void An_unreachable_server_is_reported_instead_of_crashing()
    {
        _client.StartAnonymousAsync(Arg.Any<CancellationToken>()).Returns(AuthResult.Fail<AuthSessionDto>(new AuthFailure(AuthFailure.Network, "offline")));
        Services.AddSingleton(_client);
        Services.AddSingleton<ISessionStore, InMemorySessionStore>();
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<SessionService>();

        var cut = Render<Account>();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.ShouldContain("Impossible de joindre"));
    }
}
