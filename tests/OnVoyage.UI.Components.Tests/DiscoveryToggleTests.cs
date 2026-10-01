using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.Core.Profile;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Platform.Contracts;
using OnVoyage.UI.Components.Shared;

namespace OnVoyage.UI.Components.Tests;

public sealed class DiscoveryToggleTests : BunitContext
{
    private readonly SimulatedLocationSource _location = new();
    private readonly ICatalogClient _catalog = Substitute.For<ICatalogClient>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero));

    private void Register(bool withStories = true)
    {
        var sessions = Substitute.For<ISessionProvider>();
        sessions.EnsureSessionAsync(Arg.Any<CancellationToken>()).Returns(new AuthSessionDto("t", DateTimeOffset.MaxValue, "r", DateTimeOffset.MaxValue, Guid.NewGuid(), true, null, []));
        var poi = new PoiSummaryDto(
            Guid.NewGuid(), "fort", "Fort Saint-Jean", "history", 43.2965, 5.37, 0.7, 0.8, 2, false, null, 90, new Dictionary<string, double> { ["history"] = 1d },
            withStories ? Guid.NewGuid() : null, false, withStories ? new Dictionary<string, string> { ["main"] = "https://media/main.mp3" } : null);
        _catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([poi]);
        var audio = new AudioPlaybackController(new SilentPlayer(), new NullAnalyticsSink(), new InMemoryFlagStore(), _clock);
        Services.AddSingleton(new DiscoveryModeController(
            _catalog, new InMemoryProfileStore(), sessions, _location, audio, new InMemoryTellHistoryStore(), new NullVisitSink(), new NoScreenKeepAwake(), new NoCallMonitor(),
            new DefaultTriggerSettingsProvider(), new NullAnalyticsSink(), _clock));
    }

    [Fact]
    public void Without_a_discovery_controller_the_component_shows_nothing()
    {
        Render<DiscoveryToggle>().Markup.Trim().ShouldBeEmpty();
    }

    [Fact]
    public void The_mode_is_off_until_the_traveler_asks_and_explains_where_the_position_stays()
    {
        Register();

        var cut = Render<DiscoveryToggle>();

        cut.Find("h2").TextContent.ShouldBe("Mode découverte");
        cut.Find(".hint").TextContent.ShouldContain("Votre position reste sur votre téléphone");
        _location.Started.ShouldBeFalse("nothing is requested before the button is pressed");
    }

    [Fact]
    public void Pressing_the_button_starts_listening_and_shows_the_status_and_a_stop_button()
    {
        Register();
        var cut = Render<DiscoveryToggle>();

        cut.Find("button.btn").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".status").TextContent.ShouldContain("À l'écoute de ce qui vous entoure (à pied)");
            cut.Find("button.btn-soft").TextContent.ShouldBe("Arrêter le mode découverte");
        });
        _location.Started.ShouldBeTrue();

        cut.Find("button.btn-soft").Click();
        cut.WaitForAssertion(() => cut.FindAll(".status").ShouldBeEmpty());
        _location.Started.ShouldBeFalse();
    }

    [Fact]
    public void A_refused_permission_explains_how_to_allow_it()
    {
        Register();
        _location.Permitted = false;
        var cut = Render<DiscoveryToggle>();

        cut.Find("button.btn").Click();

        cut.WaitForAssertion(() => cut.Find("[role=status]").TextContent.ShouldContain("réglages du téléphone"));
        cut.FindAll(".status").ShouldBeEmpty();
    }

    [Fact]
    public void A_destination_without_voiced_stories_says_so()
    {
        Register(withStories: false);
        var cut = Render<DiscoveryToggle>();

        cut.Find("button.btn").Click();

        cut.WaitForAssertion(() => cut.Find("[role=status]").TextContent.ShouldContain("Aucune histoire"));
    }
}
