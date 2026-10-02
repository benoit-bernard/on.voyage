using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Background;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.Core.Profile;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Platform.Contracts;
using OnVoyage.UI.Components.Shared;

namespace OnVoyage.UI.Components.Tests;

public sealed class BackgroundUiTests : BunitContext
{
    [Fact]
    public async Task The_explanation_appears_while_a_question_is_open_and_agreeing_answers_it()
    {
        var broker = new BackgroundRationaleBroker();
        Services.AddSingleton(broker);
        var cut = Render<BackgroundRationaleDialog>();
        cut.FindAll(".rationale").ShouldBeEmpty();

        var answer = broker.ConfirmAsync(Xunit.TestContext.Current.CancellationToken);

        cut.WaitForAssertion(() => cut.Find(".rationale").TextContent.ShouldContain("Toujours autoriser"));
        cut.Find(".rationale").TextContent.ShouldContain("n'est ni enregistrée, ni envoyée");
        cut.Find(".rationale").TextContent.ShouldContain("Vous pouvez refuser");
        cut.Find(".rationale .btn").Click();

        (await answer).ShouldBeTrue();
        cut.WaitForAssertion(() => cut.FindAll(".rationale").ShouldBeEmpty());
    }

    [Fact]
    public async Task Not_now_refuses_without_leaving_the_traveler_blocked()
    {
        var broker = new BackgroundRationaleBroker();
        Services.AddSingleton(broker);
        var cut = Render<BackgroundRationaleDialog>();
        var answer = broker.ConfirmAsync(Xunit.TestContext.Current.CancellationToken);

        cut.WaitForAssertion(() => cut.FindAll(".rationale").Count.ShouldBe(1));
        cut.Find(".rationale .btn-soft").Click();

        (await answer).ShouldBeFalse();
    }

    private (IBackgroundStatus Status, DiscoveryModeController Discovery) RegisterToggle(LocationAccess access, BackgroundAdvice advice = BackgroundAdvice.None)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero));
        var sessions = Substitute.For<ISessionProvider>();
        sessions.EnsureSessionAsync(Arg.Any<CancellationToken>()).Returns(new AuthSessionDto("t", DateTimeOffset.MaxValue, "r", DateTimeOffset.MaxValue, Guid.NewGuid(), true, null, []));
        var catalog = Substitute.For<ICatalogClient>();
        catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([
            new PoiSummaryDto(Guid.NewGuid(), "fort", "Fort", "history", 43.29, 5.37, 0.7, 0.8, 2, false, null, 90, new Dictionary<string, double> { ["history"] = 1d },
                Guid.NewGuid(), false, new Dictionary<string, string> { ["main"] = "https://media/main.mp3" })]);
        var status = Substitute.For<IBackgroundStatus>();
        status.Access.Returns(access);
        status.Advice.Returns(advice);
        var audio = new AudioPlaybackController(new SilentPlayer(), new NullAnalyticsSink(), new InMemoryFlagStore(), clock);
        var discovery = new DiscoveryModeController(
            catalog, new InMemoryProfileStore(), sessions, new SimulatedLocationSource(), audio, new InMemoryTellHistoryStore(), new NullVisitSink(), new NoScreenKeepAwake(),
            new NoCallMonitor(), new DefaultTriggerSettingsProvider(), new NullAnalyticsSink(), clock, background: status);
        Services.AddSingleton(discovery);
        Services.AddSingleton(status);
        return (status, discovery);
    }

    [Fact]
    public void With_only_the_foreground_permission_the_toggle_says_the_screen_must_stay_on()
    {
        RegisterToggle(LocationAccess.Foreground);
        var cut = Render<DiscoveryToggle>();

        cut.Find("button.btn").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("sans l'autorisation « Toujours »"));
    }

    [Fact]
    public void With_the_background_mode_the_toggle_mentions_the_notification_and_nothing_else()
    {
        RegisterToggle(LocationAccess.Background);
        var cut = Render<DiscoveryToggle>();

        cut.Find("button.btn").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("continue écran verrouillé"));
        cut.Markup.ShouldNotContain("réglages de batterie");
    }

    [Fact]
    public void The_battery_advice_offers_the_system_settings()
    {
        var (status, _) = RegisterToggle(LocationAccess.Background, BackgroundAdvice.BatteryOptimization);
        var cut = Render<DiscoveryToggle>();
        cut.Find("button.btn").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("interrompre ON.VOYAGE"));
        cut.FindAll("button.btn-soft").Single(button => button.TextContent.Contains("réglages de batterie")).Click();

        status.Received(1).OpenBatterySettingsAsync();
    }
}
