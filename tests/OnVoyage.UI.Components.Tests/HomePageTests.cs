using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OnVoyage.App.Core;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Home;
using OnVoyage.App.Core.Profile;
using OnVoyage.Catalog.Contracts;
using OnVoyage.UI.Components.Pages;

namespace OnVoyage.UI.Components.Tests;

public sealed class HomePageTests : BunitContext
{
    private static PoiSummaryDto Poi(string name, bool gem, double importance = 0.8) =>
        new(Guid.CreateVersion7(), name.ToLowerInvariant(), name, "history", 43.3, 5.4, importance, 0.8, gem ? 1 : 4, gem, null, 90, new Dictionary<string, double> { ["history"] = 1d });

    private void Arrange(Exception? failure = null)
    {
        var client = Substitute.For<ICatalogClient>();
        if (failure is null)
        {
            client.GetDestinationAsync("marseille", Arg.Any<CancellationToken>()).Returns(new DestinationDto("marseille", "Marseille", 43.3, 5.4, 2));
            client.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([.. Enumerable.Range(1, 6).Select(i => Poi($"Lieu{i}", false)), Poi("Vallon", true, 0.1)]);
        }
        else
        {
            client.GetDestinationAsync("marseille", Arg.Any<CancellationToken>()).Returns<DestinationDto?>(_ => throw failure);
        }

        Services.AddSingleton(client);
        // A traveler outside the 20 % control cohort, so the personalised ranking (not the id-ordered control list) is shown.
        var store = new InMemoryProfileStore();
        var profile = new LocalProfile();
        while (OnVoyage.Recommendation.Engine.ControlCohort.Contains(profile.TravelerId))
        {
            profile = new LocalProfile();
        }

        store.SaveAsync(profile, CancellationToken.None).GetAwaiter().GetResult();
        Services.AddSingleton<IProfileStore>(store);
        Services.AddSingleton<ILocationProvider, NoLocationProvider>();
        Services.AddSingleton<HomeFeedService>();
    }

    [Fact]
    public void Home_shows_destination_sections_and_places()
    {
        Arrange();

        var cut = Render<Home>();
        cut.WaitForAssertion(() => cut.Find("h1").TextContent.ShouldBe("Bonjour, Marseille"));

        cut.Markup.ShouldContain("Lieu1");
        cut.Markup.ShouldContain("Moins fréquenté, tout aussi beau");
        cut.Markup.ShouldContain("Dites-nous ce que vous aimez");
    }

    [Fact]
    public void Home_offers_a_retry_when_the_backend_is_unreachable()
    {
        Arrange(new HttpRequestException("down"));

        var cut = Render<Home>();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.ShouldContain("Impossible de joindre"));
        cut.Find("button").TextContent.ShouldBe("Réessayer");
    }

    [Fact]
    public void Onboarding_chips_cycle_like_dislike_neutral_and_are_accessible()
    {
        Services.AddSingleton<IProfileStore, InMemoryProfileStore>();
        var cut = Render<Onboarding>();
        var chip = cut.FindAll("button.chip")[0];

        chip.GetAttribute("aria-pressed").ShouldBe("false");
        chip.Click();
        cut.FindAll("button.chip")[0].GetAttribute("aria-pressed").ShouldBe("true");
        cut.FindAll("button.chip")[0].ClassList.ShouldContain("chip-like");
        cut.FindAll("button.chip")[0].Click();
        cut.FindAll("button.chip")[0].ClassList.ShouldContain("chip-dislike");
        cut.FindAll("button.chip")[0].Click();
        cut.FindAll("button.chip")[0].GetAttribute("aria-pressed").ShouldBe("false");
    }
}
