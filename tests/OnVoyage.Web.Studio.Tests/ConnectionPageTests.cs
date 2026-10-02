using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OnVoyage.Creators.Contracts;
using OnVoyage.Web.Studio.Api;
using OnVoyage.Web.Studio.Auth;
using OnVoyage.Web.Studio.Components.Pages;

namespace OnVoyage.Web.Studio.Tests;

/// <summary>T-1207 and T-1208: the connections screen and the page the platform sends the creator back to.</summary>
public sealed class ConnectionPageTests : BunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly IStudioApi _api = Substitute.For<IStudioApi>();

    public ConnectionPageTests()
    {
        Services.AddSingleton(_api);
        var auth = AddAuthorization();
        auth.SetAuthorized("marie@onvoyage.test");
        auth.SetClaims(new Claim(StudioClaims.Roles, "creator"));
    }

    private static ConnectedAccountDto Account(string platform, string status = "active", DateTimeOffset? lastSync = null, int contents = 12) =>
        new(Guid.NewGuid(), platform, "@marieenprovence", status, lastSync, contents, null, []);

    private static ConnectionsDto Connections(ConnectedAccountDto? instagram = null, ConnectedAccountDto? youtube = null, bool instagramEnabled = true, bool youtubeEnabled = true) =>
        new([new ConnectionPlatformDto("instagram", instagramEnabled, instagram), new ConnectionPlatformDto("youtube", youtubeEnabled, youtube)]);

    [Fact]
    public void While_the_application_review_is_pending_the_platform_cannot_be_connected_and_the_url_alternative_is_offered()
    {
        _api.GetConnectionsAsync(Arg.Any<CancellationToken>()).Returns(Connections(instagramEnabled: false, youtubeEnabled: false));

        var cut = Render<Connections>();

        cut.WaitForAssertion(() => cut.Find("#disabled-instagram").TextContent.ShouldContain("pas encore ouvert"));
        cut.Find("#disabled-youtube a").GetAttribute("href").ShouldBe("/studio/contents");
        cut.Find("#connect-instagram").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("#connect-youtube").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("#platform-instagram").TextContent.ShouldContain("Comptes professionnels");
    }

    [Fact]
    public void Connecting_a_platform_sends_the_browser_to_its_authorization_page()
    {
        _api.GetConnectionsAsync(Arg.Any<CancellationToken>()).Returns(Connections());
        _api.StartConnectionAsync("youtube", Arg.Any<CancellationToken>()).Returns(new ConnectionStartDto("https://accounts.google.com/o/oauth2/v2/auth?state=abc"));
        var cut = Render<Connections>();
        cut.WaitForAssertion(() => cut.Find("#connect-youtube"));

        cut.Find("#connect-youtube").Click();

        cut.WaitForAssertion(() => Services.GetRequiredService<NavigationManager>().Uri.ShouldBe("https://accounts.google.com/o/oauth2/v2/auth?state=abc"));
    }

    [Fact]
    public void A_connected_account_shows_its_name_its_contents_and_its_last_synchronisation_and_can_be_synchronised_again()
    {
        _api.GetConnectionsAsync(Arg.Any<CancellationToken>()).Returns(Connections(youtube: Account("youtube", lastSync: Now, contents: 37)));
        _api.RequestSyncAsync(Arg.Any<CancellationToken>()).Returns(new SyncRequestedDto(1));
        var cut = Render<Connections>();

        cut.WaitForAssertion(() => cut.Find("#count-youtube").TextContent.ShouldContain("37 contenu(s)"));
        cut.Find("#platform-youtube strong").TextContent.ShouldBe("@marieenprovence");
        cut.Find("#last-sync-youtube").TextContent.ShouldContain("02/10/2026 09:00");
        cut.Find("#platform-youtube .status").TextContent.ShouldBe("Connecté");
        cut.FindAll("#connect-youtube").ShouldBeEmpty();
        cut.Find("#resync").HasAttribute("disabled").ShouldBeFalse();

        cut.Find("#resync").Click();

        cut.WaitForAssertion(() => cut.Find("p.alert.ok").TextContent.ShouldContain("Synchronisation demandée"));
    }

    [Fact]
    public void A_never_synchronised_account_says_the_first_import_is_under_way()
    {
        _api.GetConnectionsAsync(Arg.Any<CancellationToken>()).Returns(Connections(instagram: Account("instagram", lastSync: null, contents: 0)));

        var cut = Render<Connections>();

        cut.WaitForAssertion(() => cut.Find("#last-sync-instagram").TextContent.ShouldBe("première synchronisation en cours"));
    }

    [Fact]
    public void An_account_the_platform_no_longer_accepts_asks_to_be_connected_again()
    {
        _api.GetConnectionsAsync(Arg.Any<CancellationToken>()).Returns(Connections(youtube: Account("youtube", status: "needs_reauth", lastSync: Now)));
        _api.StartConnectionAsync("youtube", Arg.Any<CancellationToken>()).Returns(new ConnectionStartDto("https://accounts.google.com/auth"));

        var cut = Render<Connections>();

        cut.WaitForAssertion(() => cut.Find("#reauth-youtube").TextContent.ShouldContain("reconnectez le compte"));
        cut.Find("#platform-youtube .status").TextContent.ShouldBe("À reconnecter");
        cut.Find("#resync").HasAttribute("disabled").ShouldBeTrue(); // nothing active to synchronise
        cut.Find("#reconnect-youtube").Click();
        cut.WaitForAssertion(() => _api.Received(1).StartConnectionAsync("youtube", Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void Disconnecting_asks_whether_the_imported_contents_go_too()
    {
        _api.GetConnectionsAsync(Arg.Any<CancellationToken>()).Returns(Connections(youtube: Account("youtube")), Connections());
        var cut = Render<Connections>();
        cut.WaitForAssertion(() => cut.Find("#disconnect-youtube"));

        cut.Find("#delete-contents-youtube").Change(true);
        cut.Find("#disconnect-youtube").Click();

        cut.WaitForAssertion(() => _api.Received(1).DisconnectAsync("youtube", true, Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() => cut.Find("p.alert.ok").TextContent.ShouldContain("jetons sont supprimés"));
        cut.WaitForAssertion(() => cut.Find("#connect-youtube"));
    }

    [Fact]
    public void Disconnecting_without_the_option_keeps_the_contents()
    {
        _api.GetConnectionsAsync(Arg.Any<CancellationToken>()).Returns(Connections(instagram: Account("instagram")));
        var cut = Render<Connections>();
        cut.WaitForAssertion(() => cut.Find("#disconnect-instagram"));

        cut.Find("#disconnect-instagram").Click();

        cut.WaitForAssertion(() => _api.Received(1).DisconnectAsync("instagram", false, Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void The_refusal_of_a_personal_instagram_account_is_shown_as_the_service_explains_it()
    {
        _api.CompleteConnectionAsync("instagram", "c", "s", Arg.Any<CancellationToken>())
            .Returns<ConnectedAccountDto>(_ => throw new StudioApiException("Compte professionnel requis : passez votre compte Instagram en compte professionnel.", 422));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/studio/connections/instagram/callback?code=c&state=s");

        var page = Render<ConnectionCallback>(parameters => parameters.Add(item => item.Platform, "instagram"));

        page.WaitForAssertion(() => page.Find("p.alert.error").TextContent.ShouldContain("Compte professionnel requis"));
        page.Find("a").GetAttribute("href").ShouldBe("/studio/connections");
    }

    [Fact]
    public void A_successful_return_connects_the_account_once_and_goes_back_to_the_connections()
    {
        _api.CompleteConnectionAsync("youtube", "the-code", "the-state", Arg.Any<CancellationToken>()).Returns(Account("youtube"));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/studio/connections/youtube/callback?code=the-code&state=the-state");

        Render<ConnectionCallback>(parameters => parameters.Add(page => page.Platform, "youtube"));

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/studio/connections?connected=youtube");
        _api.Received(1).CompleteConnectionAsync("youtube", "the-code", "the-state", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_return_without_code_or_with_an_error_connects_nothing_and_says_so()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/studio/connections/youtube/callback?error=access_denied&state=s");

        Render<ConnectionCallback>(parameters => parameters.Add(page => page.Platform, "youtube"));

        Services.GetRequiredService<NavigationManager>().Uri.ShouldContain("/studio/connections?error=");
        _api.DidNotReceiveWithAnyArgs().CompleteConnectionAsync(default!, default!, default!, Ct);
    }
}
