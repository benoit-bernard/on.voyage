using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.Core.Driving;
using OnVoyage.App.Core.Profile;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Platform.Contracts;
using OnVoyage.UI.Components.Pages;
using OnVoyage.UI.Components.Shared;

namespace OnVoyage.UI.Components.Tests;

public sealed class CarModeTests : BunitContext
{
    private const double Lat = 43.2965;
    private const double Lon = 5.37;

    private readonly SimulatedLocationSource _location = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero));
    private readonly SilentPlayer _player = new();
    private CarModeController _car = default!;
    private DiscoveryModeController _discovery = default!;

    private void Register(bool withStories = true)
    {
        var sessions = Substitute.For<ISessionProvider>();
        sessions.EnsureSessionAsync(Arg.Any<CancellationToken>()).Returns(new AuthSessionDto("t", DateTimeOffset.MaxValue, "r", DateTimeOffset.MaxValue, Guid.NewGuid(), true, null, []));
        var catalog = Substitute.For<ICatalogClient>();
        var poi = new PoiSummaryDto(
            Guid.NewGuid(), "belvedere", "Belvédère de la Corniche", "nature", Lat, Lon + 0.04, 0.8, 0.8, 2, false, null, 90, new Dictionary<string, double> { ["nature"] = 1d },
            withStories ? Guid.NewGuid() : null, false, withStories ? new Dictionary<string, string> { ["main"] = "https://media/main.mp3" } : null);
        catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([poi]);
        var audio = new AudioPlaybackController(_player, new NullAnalyticsSink(), new InMemoryFlagStore(), _clock);
        _discovery = new DiscoveryModeController(
            catalog, new InMemoryProfileStore(), sessions, _location, audio, new InMemoryTellHistoryStore(), new NullVisitSink(), new NoScreenKeepAwake(), new NoCallMonitor(),
            new DefaultTriggerSettingsProvider(), new NullAnalyticsSink(), _clock);
        _car = new CarModeController(_discovery, audio, new CarModeState());
        Services.AddSingleton(audio);
        Services.AddSingleton(_discovery);
        Services.AddSingleton(_car);
    }

    [Fact]
    public void Without_a_car_controller_the_page_says_it_is_unavailable()
    {
        var cut = Render<CarMode>();

        cut.Markup.ShouldContain("n'est pas disponible");
    }

    [Fact]
    public void The_page_explains_that_nothing_is_asked_while_driving_and_asks_nothing_before_the_button()
    {
        Register();

        var cut = Render<CarMode>();

        cut.Find(".hint").TextContent.ShouldContain("Aucune question ne vous est posée");
        cut.Find("button.start").TextContent.ShouldBe("Activer le mode voiture");
        _location.Started.ShouldBeFalse();
    }

    [Fact]
    public void Activating_shows_the_three_large_controls_and_the_next_story()
    {
        Register();
        var cut = Render<CarMode>();

        cut.Find("button.start").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".controls button").Select(button => button.GetAttribute("aria-label") ?? button.TextContent.Trim()).ShouldBe(["Lecture", "Suivante", "Arrêter"]);
            cut.FindAll(".controls button").ShouldAllBe(button => button.ClassList.Contains("big"));
        });
        _location.Started.ShouldBeTrue();
    }

    [Fact]
    public void A_refused_permission_explains_how_to_allow_it()
    {
        Register();
        _location.Permitted = false;
        var cut = Render<CarMode>();

        cut.Find("button.start").Click();

        cut.WaitForAssertion(() => cut.Find("[role=status]").TextContent.ShouldContain("réglages du téléphone"));
        cut.FindAll(".controls").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_page_shows_the_next_story_and_its_distance_while_driving()
    {
        Register();
        var cut = Render<CarMode>();
        cut.Find("button.start").Click();
        cut.WaitForAssertion(() => cut.FindAll(".controls").Count.ShouldBe(1));

        for (var i = 0; i < 5; i++)
        {
            _location.Emit(new LocationFix(Lat, Lon + (i * 20d / 80_500d), 8, 20, 90, _clock.GetUtcNow().AddSeconds(i)));
            await _discovery.LastHandling;
        }

        cut.WaitForAssertion(() =>
        {
            cut.Find(".next").TextContent.ShouldContain("Belvédère de la Corniche");
            cut.Find(".next .distance").TextContent.ShouldContain("km");
        });
    }

    [Fact]
    public void Stop_returns_to_the_start_screen_and_stops_listening()
    {
        Register();
        var cut = Render<CarMode>();
        cut.Find("button.start").Click();
        cut.WaitForAssertion(() => cut.FindAll(".controls").Count.ShouldBe(1));

        cut.Find("button.stop").Click();

        cut.WaitForAssertion(() => cut.FindAll("button.start").Count.ShouldBe(1));
        _location.Started.ShouldBeFalse();
    }

    [Fact]
    public async Task The_suggestion_appears_when_the_traveler_drives_and_declining_hides_it()
    {
        Register();
        await _discovery.StartAsync(false, Xunit.TestContext.Current.CancellationToken);
        var cut = Render<CarModeSuggestion>();
        cut.FindAll(".suggestion").ShouldBeEmpty();

        for (var i = 0; i < 5; i++)
        {
            _location.Emit(new LocationFix(Lat, Lon + (i * 20d / 80_500d), 8, 20, 90, _clock.GetUtcNow().AddSeconds(i)));
            await _discovery.LastHandling;
        }

        cut.WaitForAssertion(() => cut.Find(".suggestion").TextContent.ShouldContain("Vous roulez"));
        cut.Find(".suggestion .btn-soft").Click();
        cut.WaitForAssertion(() => cut.FindAll(".suggestion").ShouldBeEmpty());
    }

    [Fact]
    public async Task Accepting_the_suggestion_opens_the_car_screen()
    {
        Register();
        await _discovery.StartAsync(false, Xunit.TestContext.Current.CancellationToken);
        var cut = Render<CarModeSuggestion>();
        for (var i = 0; i < 5; i++)
        {
            _location.Emit(new LocationFix(Lat, Lon + (i * 20d / 80_500d), 8, 20, 90, _clock.GetUtcNow().AddSeconds(i)));
            await _discovery.LastHandling;
        }

        cut.WaitForAssertion(() => cut.FindAll(".suggestion").Count.ShouldBe(1));
        cut.Find(".suggestion .btn").Click();

        _car.IsActive.ShouldBeTrue();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri.ShouldEndWith("voiture");
    }
}
