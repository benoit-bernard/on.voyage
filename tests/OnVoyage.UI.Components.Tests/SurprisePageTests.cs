using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OnVoyage.App.Core;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Interactions;
using OnVoyage.App.Core.Surprise;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Discovery.Contracts;
using OnVoyage.UI.Components.Pages;

namespace OnVoyage.UI.Components.Tests;

public sealed class SurprisePageTests : BunitContext
{
    private readonly IDiscoveryClient _discovery = Substitute.For<IDiscoveryClient>();
    private readonly ICatalogClient _catalog = Substitute.For<ICatalogClient>();

    public SurprisePageTests()
    {
        Services.AddSingleton(new SurpriseService(_discovery, _catalog, new NullAnalyticsSink()));
        Services.AddSingleton<ILocationProvider, NoLocationProvider>();
    }

    private static RecommendationItemDto Item(string name, bool exploration) =>
        new(Guid.NewGuid(), name.ToLowerInvariant(), name, 0.7, 70, new WhyDto("hidden_gem", new Dictionary<string, string>()), exploration, null);

    [Fact]
    public void The_page_draws_at_once_and_shows_the_place_the_reason_and_the_links()
    {
        _discovery.GetSurpriseAsync(Arg.Any<double?>(), Arg.Any<double?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Item("Fort", exploration: true));

        var cut = Render<SurprisePage>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("h2").TextContent.ShouldBe("Fort");
            cut.Find(".explanation").TextContent.ShouldContain("Moins fréquenté");
            cut.Find(".badge").TextContent.ShouldBe("Hors de vos habitudes");
            cut.Find("a.btn").GetAttribute("href").ShouldBe("lieu/fort");
        });
    }

    [Fact]
    public void Another_surprise_draws_again()
    {
        var calls = 0;
        _discovery.GetSurpriseAsync(Arg.Any<double?>(), Arg.Any<double?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Item(calls++ == 0 ? "Premier" : "Second", exploration: false));
        var cut = Render<SurprisePage>();
        cut.WaitForAssertion(() => cut.Find("h2").TextContent.ShouldBe("Premier"));

        cut.Find("button.btn-soft").Click();

        cut.WaitForAssertion(() => cut.Find("h2").TextContent.ShouldBe("Second"));
    }

    [Fact]
    public void When_nothing_is_left_the_page_says_so_and_offers_the_map()
    {
        _discovery.GetSurpriseAsync(Arg.Any<double?>(), Arg.Any<double?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((RecommendationItemDto?)null);

        var cut = Render<SurprisePage>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[role=status]").TextContent.ShouldContain("fait le tour");
            cut.Find("a.btn").GetAttribute("href").ShouldBe("carte");
        });
    }

    [Fact]
    public void A_network_failure_offers_to_retry()
    {
        _discovery.GetSurpriseAsync(Arg.Any<double?>(), Arg.Any<double?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException());

        var cut = Render<SurprisePage>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[role=alert]").TextContent.ShouldContain("Vérifiez votre connexion");
            cut.Find("button.btn").TextContent.ShouldBe("Réessayer");
        });
    }
}
