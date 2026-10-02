#pragma warning disable xUnit1051 // driven against in-memory fakes
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Background;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.Core.Tests.Audio;

namespace OnVoyage.App.Core.Tests.Background;

public sealed class BackgroundAccessFlowTests
{
    private readonly ILocationPermissions _permissions = Substitute.For<ILocationPermissions>();
    private readonly IBackgroundRationale _rationale = Substitute.For<IBackgroundRationale>();
    private readonly RecordingAnalytics _analytics = new();
    private readonly BackgroundAccessFlow _flow;

    public BackgroundAccessFlowTests()
    {
        _flow = new BackgroundAccessFlow(_permissions, _rationale, _analytics);
        _permissions.CheckAsync(Arg.Any<CancellationToken>()).Returns(LocationPermissionLevel.Denied);
    }

    private IEnumerable<string?> Levels() => _analytics.Events.Where(e => e.Name == "location_permission_result").Select(e => e.Properties["level"]?.ToString());

    [Fact]
    public void Nothing_is_asked_when_the_flow_is_built()
    {
        _permissions.ReceivedCalls().Where(call => call.GetMethodInfo().Name.StartsWith("Request", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refusal_of_the_basic_permission_ends_the_flow_without_any_explanation()
    {
        _permissions.RequestWhenInUseAsync(Arg.Any<CancellationToken>()).Returns(LocationPermissionLevel.Denied);

        (await _flow.RequestAsync(wantsBackground: true, CancellationToken.None)).ShouldBe(LocationAccess.Denied);

        await _rationale.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
        Levels().ShouldBe(["denied"]);
    }

    [Fact]
    public async Task Without_a_background_session_only_the_foreground_permission_is_requested()
    {
        _permissions.RequestWhenInUseAsync(Arg.Any<CancellationToken>()).Returns(LocationPermissionLevel.WhenInUse);

        (await _flow.RequestAsync(wantsBackground: false, CancellationToken.None)).ShouldBe(LocationAccess.Foreground);

        await _permissions.DidNotReceive().RequestAlwaysAsync(Arg.Any<CancellationToken>());
        await _rationale.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Always_is_asked_after_the_explanation_and_only_then()
    {
        _permissions.RequestWhenInUseAsync(Arg.Any<CancellationToken>()).Returns(LocationPermissionLevel.WhenInUse);
        _rationale.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(true);
        _permissions.RequestAlwaysAsync(Arg.Any<CancellationToken>()).Returns(LocationPermissionLevel.Always);

        (await _flow.RequestAsync(wantsBackground: true, CancellationToken.None)).ShouldBe(LocationAccess.Background);

        Received.InOrder(() =>
        {
            _permissions.RequestWhenInUseAsync(Arg.Any<CancellationToken>());
            _rationale.ConfirmAsync(Arg.Any<CancellationToken>());
            _permissions.RequestAlwaysAsync(Arg.Any<CancellationToken>());
        });
        Levels().ShouldBe(["when_in_use", "always"]);
    }

    [Fact]
    public async Task Declining_the_explanation_keeps_the_foreground_mode_and_never_shows_the_system_dialog()
    {
        _permissions.RequestWhenInUseAsync(Arg.Any<CancellationToken>()).Returns(LocationPermissionLevel.WhenInUse);
        _rationale.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(false);

        (await _flow.RequestAsync(wantsBackground: true, CancellationToken.None)).ShouldBe(LocationAccess.Foreground);

        await _permissions.DidNotReceive().RequestAlwaysAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_Always_degrades_to_the_foreground_mode()
    {
        _permissions.CheckAsync(Arg.Any<CancellationToken>()).Returns(LocationPermissionLevel.WhenInUse);
        _rationale.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(true);
        _permissions.RequestAlwaysAsync(Arg.Any<CancellationToken>()).Returns(LocationPermissionLevel.WhenInUse);

        (await _flow.RequestAsync(wantsBackground: true, CancellationToken.None)).ShouldBe(LocationAccess.Foreground);

        Levels().ShouldBe(["when_in_use"]);
    }

    [Fact]
    public async Task A_traveler_who_already_granted_Always_is_not_asked_again()
    {
        _permissions.CheckAsync(Arg.Any<CancellationToken>()).Returns(LocationPermissionLevel.Always);

        (await _flow.RequestAsync(wantsBackground: true, CancellationToken.None)).ShouldBe(LocationAccess.Background);

        await _rationale.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
        _analytics.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_reported_events_carry_the_level_and_nothing_else()
    {
        _permissions.RequestWhenInUseAsync(Arg.Any<CancellationToken>()).Returns(LocationPermissionLevel.WhenInUse);

        await _flow.RequestAsync(wantsBackground: false, CancellationToken.None);

        _analytics.Events.Single().Properties.Keys.ShouldBe(["level"]);
    }
}

public sealed class BackgroundRationaleBrokerTests
{
    [Fact]
    public async Task The_question_stays_open_until_the_traveler_answers()
    {
        var broker = new BackgroundRationaleBroker();
        var changes = 0;
        broker.Changed += () => changes++;

        var answer = broker.ConfirmAsync(CancellationToken.None);

        broker.Pending.ShouldBeTrue();
        answer.IsCompleted.ShouldBeFalse();

        broker.Answer(true);

        (await answer).ShouldBeTrue();
        broker.Pending.ShouldBeFalse();
        changes.ShouldBe(2);
    }

    [Fact]
    public async Task Cancelling_the_start_closes_the_question_as_a_refusal()
    {
        var broker = new BackgroundRationaleBroker();
        using var cancellation = new CancellationTokenSource();

        var answer = broker.ConfirmAsync(cancellation.Token);
        await cancellation.CancelAsync();

        (await answer).ShouldBeFalse();
        broker.Pending.ShouldBeFalse();
    }
}

public sealed class BackgroundHealthMonitorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 14, 9, 0, 0, TimeSpan.Zero);
    private readonly BackgroundHealthMonitor _monitor = new(new BackgroundHealthSettings());

    [Fact]
    public void Steady_fixes_never_raise_anything()
    {
        _monitor.Begin(T0);
        for (var s = 0; s < 600; s += 15)
        {
            _monitor.OnFix(T0.AddSeconds(s));
            _monitor.Evaluate(T0.AddSeconds(s), systemRestrictsApp: true).ShouldBe(HealthAction.None);
        }
    }

    [Fact]
    public void A_silence_restarts_the_updates_with_a_doubling_delay_then_advises_once()
    {
        _monitor.Begin(T0);

        _monitor.Evaluate(T0.AddSeconds(89), true).ShouldBe(HealthAction.None);
        _monitor.Evaluate(T0.AddSeconds(90), true).ShouldBe(HealthAction.RestartUpdates);
        _monitor.Evaluate(T0.AddSeconds(179), true).ShouldBe(HealthAction.None);
        _monitor.Evaluate(T0.AddSeconds(180), true).ShouldBe(HealthAction.RestartUpdates);
        _monitor.Evaluate(T0.AddSeconds(359), true).ShouldBe(HealthAction.None);
        _monitor.Evaluate(T0.AddSeconds(360), true).ShouldBe(HealthAction.RestartUpdates);
        _monitor.Evaluate(T0.AddSeconds(720), true).ShouldBe(HealthAction.AdviseBatteryOptimization);
        _monitor.Evaluate(T0.AddSeconds(1500), true).ShouldBe(HealthAction.None, "advised once per session");
    }

    [Fact]
    public void A_system_without_restriction_is_never_blamed()
    {
        _monitor.Begin(T0);
        _monitor.Evaluate(T0.AddSeconds(90), false);
        _monitor.Evaluate(T0.AddSeconds(180), false);
        _monitor.Evaluate(T0.AddSeconds(360), false);

        _monitor.Evaluate(T0.AddSeconds(5000), false).ShouldBe(HealthAction.None);
    }

    [Fact]
    public void A_fix_resets_the_attempts()
    {
        _monitor.Begin(T0);
        _monitor.Evaluate(T0.AddSeconds(90), true).ShouldBe(HealthAction.RestartUpdates);

        _monitor.OnFix(T0.AddSeconds(100));

        _monitor.Evaluate(T0.AddSeconds(150), true).ShouldBe(HealthAction.None);
        _monitor.Evaluate(T0.AddSeconds(190), true).ShouldBe(HealthAction.RestartUpdates, "90 s after the last fix again");
    }
}

public sealed class BackgroundLocationSourceTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero));
    private readonly IPlatformLocationUpdates _platform = Substitute.For<IPlatformLocationUpdates>();
    private readonly ILocationPermissions _permissions = Substitute.For<ILocationPermissions>();
    private readonly IBackgroundRationale _rationale = Substitute.For<IBackgroundRationale>();
    private readonly IBackgroundSession _session = Substitute.For<IBackgroundSession>();
    private readonly IBatteryOptimization _battery = Substitute.For<IBatteryOptimization>();
    private readonly BackgroundLocationSource _source;

    public BackgroundLocationSourceTests()
    {
        _session.IsSupported.Returns(true);
        _permissions.CheckAsync(Arg.Any<CancellationToken>()).Returns(LocationPermissionLevel.Always);
        _platform.StartAsync(Arg.Any<LocationUpdateMode>(), Arg.Any<CancellationToken>()).Returns(true);
        _battery.IsRestricted.Returns(true);
        _source = new BackgroundLocationSource(_platform, new BackgroundAccessFlow(_permissions, _rationale, new NullAnalyticsSink()), _session, _battery, new NullAnalyticsSink(), _clock);
    }

    public void Dispose() => _source.Dispose();

    [Fact]
    public async Task With_Always_the_session_starts_before_the_updates_which_run_in_the_background_mode()
    {
        (await _source.StartAsync(CancellationToken.None)).ShouldBeTrue();

        _source.Access.ShouldBe(LocationAccess.Background);
        Received.InOrder(() =>
        {
            _session.StartAsync(BackgroundSessionRequest.Default, Arg.Any<CancellationToken>());
            _platform.StartAsync(LocationUpdateMode.Background, Arg.Any<CancellationToken>());
        });
        BackgroundSessionRequest.Default.Title.ShouldBe("ON.VOYAGE vous accompagne");
        BackgroundSessionRequest.Default.StopLabel.ShouldBe("Arrêter");
    }

    [Fact]
    public async Task A_platform_without_background_session_runs_in_the_foreground()
    {
        _session.IsSupported.Returns(false);
        _permissions.CheckAsync(Arg.Any<CancellationToken>()).Returns(LocationPermissionLevel.WhenInUse);

        (await _source.StartAsync(CancellationToken.None)).ShouldBeTrue();

        _source.Access.ShouldBe(LocationAccess.Foreground);
        await _session.DidNotReceive().StartAsync(Arg.Any<BackgroundSessionRequest>(), Arg.Any<CancellationToken>());
        await _platform.Received().StartAsync(LocationUpdateMode.Foreground, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_background_permission_degrades_to_the_foreground_mode()
    {
        _permissions.CheckAsync(Arg.Any<CancellationToken>()).Returns(LocationPermissionLevel.WhenInUse);
        _rationale.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(false);

        (await _source.StartAsync(CancellationToken.None)).ShouldBeTrue();

        _source.Access.ShouldBe(LocationAccess.Foreground);
        await _platform.Received().StartAsync(LocationUpdateMode.Foreground, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_location_permission_starts_nothing()
    {
        _permissions.CheckAsync(Arg.Any<CancellationToken>()).Returns(LocationPermissionLevel.Denied);
        _permissions.RequestWhenInUseAsync(Arg.Any<CancellationToken>()).Returns(LocationPermissionLevel.Denied);

        (await _source.StartAsync(CancellationToken.None)).ShouldBeFalse();

        _source.Access.ShouldBe(LocationAccess.Denied);
        await _session.DidNotReceive().StartAsync(Arg.Any<BackgroundSessionRequest>(), Arg.Any<CancellationToken>());
        await _platform.DidNotReceive().StartAsync(Arg.Any<LocationUpdateMode>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_the_system_refuses_the_service_the_foreground_mode_still_works()
    {
        _session.StartAsync(Arg.Any<BackgroundSessionRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("ForegroundServiceStartNotAllowedException"));

        (await _source.StartAsync(CancellationToken.None)).ShouldBeTrue();

        _source.Access.ShouldBe(LocationAccess.Foreground);
        await _platform.Received().StartAsync(LocationUpdateMode.Foreground, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_the_updates_cannot_start_the_session_is_stopped()
    {
        _platform.StartAsync(Arg.Any<LocationUpdateMode>(), Arg.Any<CancellationToken>()).Returns(false);

        (await _source.StartAsync(CancellationToken.None)).ShouldBeFalse();

        await _session.Received().StopAsync();
    }

    [Fact]
    public async Task Stopping_stops_the_updates_then_the_session()
    {
        await _source.StartAsync(CancellationToken.None);

        await _source.StopAsync();

        Received.InOrder(() =>
        {
            _platform.StopAsync();
            _session.StopAsync();
        });
        _source.Access.ShouldBe(LocationAccess.Denied);
    }

    [Fact]
    public async Task Fixes_are_relayed_to_the_discovery_mode()
    {
        await _source.StartAsync(CancellationToken.None);
        LocationFix? received = null;
        _source.FixReceived += fix => received = fix;
        var sent = new LocationFix(43.29, 5.37, 10, 1.2, 90, _clock.GetUtcNow());

        _platform.FixReceived += Raise.Event<Action<LocationFix>>(sent);

        received.ShouldBe(sent);
    }

    [Fact]
    public async Task A_silent_background_asks_for_updates_again_then_advises_the_battery_settings_once()
    {
        await _source.StartAsync(CancellationToken.None);
        var changes = 0;
        _source.Changed += () => changes++;

        for (var seconds = 0; seconds < 800; seconds += 15)
        {
            _clock.Advance(TimeSpan.FromSeconds(15));
        }

        await _platform.Received(3).RestartAsync(Arg.Any<CancellationToken>());
        _source.Advice.ShouldBe(BackgroundAdvice.BatteryOptimization);
        changes.ShouldBe(1);

        await _source.OpenBatterySettingsAsync();
        await _battery.Received(1).OpenSettingsAsync();
    }

    [Fact]
    public async Task A_foreground_only_run_is_not_watched()
    {
        _session.IsSupported.Returns(false);
        await _source.StartAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromHours(1));

        await _platform.DidNotReceive().RestartAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_in_the_notification_is_forwarded()
    {
        await _source.StartAsync(CancellationToken.None);
        var stops = 0;
        _source.StopRequested += () => stops++;

        _session.StopRequested += Raise.Event<Action>();

        stops.ShouldBe(1);
    }
}

public sealed class StopFromNotificationTests
{
    [Fact]
    public async Task Pressing_Stop_in_the_notification_ends_the_discovery_mode()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero));
        var catalog = Substitute.For<OnVoyage.App.Core.Catalog.ICatalogClient>();
        catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([
            new OnVoyage.Catalog.Contracts.PoiSummaryDto(Guid.NewGuid(), "fort", "Fort", "history", 43.29, 5.37, 0.7, 0.8, 2, false, null, 90, new Dictionary<string, double> { ["history"] = 1d },
                Guid.NewGuid(), false, new Dictionary<string, string> { ["main"] = "https://media/main.mp3" })]);
        var sessions = Substitute.For<OnVoyage.App.Core.Auth.ISessionProvider>();
        sessions.EnsureSessionAsync(Arg.Any<CancellationToken>()).Returns(new OnVoyage.Platform.Contracts.AuthSessionDto("t", DateTimeOffset.MaxValue, "r", DateTimeOffset.MaxValue, Guid.NewGuid(), true, null, []));
        var location = new SimulatedLocationSource();
        var background = Substitute.For<IBackgroundStatus>();
        var audio = new AudioPlaybackController(new FakeAudioPlayer(), new NullAnalyticsSink(), new InMemoryFlagStore(), clock);
        using var discovery = new DiscoveryModeController(
            catalog, new OnVoyage.App.Core.Profile.InMemoryProfileStore(), sessions, location, audio, new InMemoryTellHistoryStore(), new NullVisitSink(), new NoScreenKeepAwake(),
            new NoCallMonitor(), new DefaultTriggerSettingsProvider(), new NullAnalyticsSink(), clock, background: background);
        await discovery.StartAsync(false);
        discovery.State.IsOn.ShouldBeTrue();

        background.StopRequested += Raise.Event<Action>();
        await Task.Delay(50);

        discovery.State.IsOn.ShouldBeFalse();
        location.Started.ShouldBeFalse();
    }
}
