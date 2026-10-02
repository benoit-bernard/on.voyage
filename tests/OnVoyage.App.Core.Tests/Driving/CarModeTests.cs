#pragma warning disable xUnit1051 // driven against in-memory fakes
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.Core.Driving;
using OnVoyage.App.Core.Feedback;
using OnVoyage.App.Core.Interactions;
using OnVoyage.App.Core.Profile;
using OnVoyage.App.Core.Tests.Audio;
using OnVoyage.App.Core.Tests.Discovery;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.App.Core.Tests.Driving;

public sealed class CarModeTests : IDisposable
{
    private const double Lat = 43.2965;
    private const double Lon = 5.3700;
    private const double MetersPerDegreeLat = 111_320d;
    private const double CarSpeed = 20d; // 72 km/h

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero));
    private readonly ICatalogClient _catalog = Substitute.For<ICatalogClient>();
    private readonly InMemoryProfileStore _profiles = new();
    private readonly SimulatedLocationSource _location = new();
    private readonly FakeAudioPlayer _player = new();
    private readonly AudioPlaybackController _audio;
    private readonly DiscoveryModeController _discovery;
    private readonly CarModeState _state = new();
    private readonly CarModeController _car;
    private readonly CapturingNotifier _notifier = new();
    private readonly FeedbackTracker _tracker;

    private sealed class CapturingNotifier : ILocalNotifier
    {
        public List<string> Bodies { get; } = [];

        public Task NotifyAsync(string title, string body, CancellationToken cancellationToken)
        {
            Bodies.Add(body);
            return Task.CompletedTask;
        }
    }

    private sealed class NoOutbox : IInteractionOutbox
    {
        public Task EnqueueAsync(InteractionDto interaction, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public CarModeTests()
    {
        LocalProfile profile = new();
        while (OnVoyage.Recommendation.Engine.ControlCohort.Contains(profile.TravelerId))
        {
            profile = new LocalProfile();
        }

        _profiles.SaveAsync(profile, CancellationToken.None).GetAwaiter().GetResult();
        var sessions = Substitute.For<ISessionProvider>();
        sessions.EnsureSessionAsync(Arg.Any<CancellationToken>()).Returns(new AuthSessionDto("t", DateTimeOffset.MaxValue, "r", DateTimeOffset.MaxValue, profile.TravelerId, true, null, []));
        var analytics = new RecordingAnalytics();
        _audio = new AudioPlaybackController(_player, analytics, new InMemoryFlagStore(), _clock);
        _discovery = new DiscoveryModeController(
            _catalog, _profiles, sessions, _location, _audio, new InMemoryTellHistoryStore(), new NullVisitSink(), new NoScreenKeepAwake(), new NoCallMonitor(),
            new DefaultTriggerSettingsProvider(), analytics, _clock);
        _car = new CarModeController(_discovery, _audio, _state);
        _tracker = new FeedbackTracker(_audio, new InteractionRecorder(_profiles, new NoOutbox(), _clock), _notifier, _clock, _state);
    }

    public void Dispose()
    {
        _tracker.Dispose();
        _car.Dispose();
        _discovery.Dispose();
    }

    private static PoiSummaryDto Poi(string name, double latitude, double longitude, double importance = 0.7) =>
        new(Guid.NewGuid(), name.ToLowerInvariant(), name, "history", latitude, longitude, importance, 0.8, 2, false, null, 90, new Dictionary<string, double> { ["history"] = 1d },
            Guid.NewGuid(), false, new Dictionary<string, string> { ["main"] = $"https://media/{name}/main.mp3" });

    private static double EastMeters(double meters) => meters / (MetersPerDegreeLat * Math.Cos(Lat * Math.PI / 180d));

    /// <summary>The traveler drives east at <see cref="CarSpeed"/>; <paramref name="from"/> is the second of the first fix, <paramref name="startMeters"/> the distance already covered.</summary>
    private async Task DriveAsync(int fixes, int from = 0, double startMeters = 0)
    {
        for (var i = 0; i < fixes; i++)
        {
            var seconds = from + i;
            _location.Emit(new LocationFix(Lat, Lon + EastMeters(startMeters + (CarSpeed * i)), 8, CarSpeed, 90, _clock.GetUtcNow().AddSeconds(seconds)));
            await _discovery.LastHandling;
        }
    }

    private async Task WalkAsync(int fixes, int from)
    {
        for (var i = 0; i < fixes; i++)
        {
            _location.Emit(new LocationFix(Lat, Lon + EastMeters(i), 8, 1.2, 90, _clock.GetUtcNow().AddSeconds(from + i)));
            await _discovery.LastHandling;
        }
    }

    [Fact]
    public async Task The_car_screen_is_proposed_once_the_engine_sees_the_traveler_driving_and_not_before()
    {
        _catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([Poi("Fort", Lat, Lon + EastMeters(20_000))]);
        await _discovery.StartAsync(false);

        await WalkAsync(5, 0);
        _car.Suggested.ShouldBeFalse("walking");

        await DriveAsync(40, from: 40, startMeters: 0);

        _discovery.State.Mode.ShouldBe(TravelMode.Car);
        _car.Suggested.ShouldBeTrue();
        _car.IsActive.ShouldBeFalse("it is only a proposal");
    }

    [Fact]
    public void Nothing_is_proposed_when_discovery_is_off()
    {
        _car.Suggested.ShouldBeFalse();
    }

    [Fact]
    public async Task Declining_the_proposal_silences_it_until_the_drive_is_over()
    {
        _catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([Poi("Fort", Lat, Lon + EastMeters(20_000))]);
        await _discovery.StartAsync(false);
        await DriveAsync(5);
        _car.Suggested.ShouldBeTrue();

        _car.DeclineSuggestion();
        await DriveAsync(5, from: 5, startMeters: 100);
        _car.Suggested.ShouldBeFalse();

        // Back to walking for longer than the hysteresis, then driving again: a new drive is proposed again.
        await WalkAsync(60, from: 100);
        _discovery.State.Mode.ShouldBe(TravelMode.Walk);
        await DriveAsync(60, from: 200);
        _discovery.State.Mode.ShouldBe(TravelMode.Car);
        _car.Suggested.ShouldBeTrue();
    }

    [Fact]
    public async Task Accepting_turns_the_car_screen_on_and_shows_the_next_story_ahead_with_a_rounded_distance()
    {
        var ahead = Poi("Belvédère", Lat, Lon + EastMeters(3_020));
        var behind = Poi("Derrière", Lat, Lon - EastMeters(500));
        _catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([ahead, behind]);
        await _discovery.StartAsync(false);
        await DriveAsync(5);

        _car.Accept();

        _car.IsActive.ShouldBeTrue();
        _car.Suggested.ShouldBeFalse();
        var view = _car.View;
        view.NextTitle.ShouldBe("Belvédère", "the place behind the car is never announced nor shown");
        view.NextDistanceMeters.ShouldBe(2950, "about 2,95 km after 80 m of road, rounded to 50 m");
        CarModeView.FormatDistance(2900).ShouldBe("2,9 km");
        CarModeView.FormatDistance(3000).ShouldBe("3 km");
        CarModeView.FormatDistance(450).ShouldBe("450 m");
    }

    [Fact]
    public async Task Activating_by_hand_starts_discovery_and_a_refused_permission_leaves_the_car_screen_off()
    {
        _catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([Poi("Fort", Lat, Lon)]);
        _location.Permitted = false;

        (await _car.ActivateAsync()).ShouldBe(CarModeStartResult.PermissionDenied);
        _car.IsActive.ShouldBeFalse();
        _discovery.State.IsOn.ShouldBeFalse();

        _location.Permitted = true;
        (await _car.ActivateAsync()).ShouldBe(CarModeStartResult.Started);
        _car.IsActive.ShouldBeTrue();
        _discovery.State.IsOn.ShouldBeTrue();
    }

    [Fact]
    public async Task Activating_with_nothing_to_tell_says_so()
    {
        _catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([]);

        (await _car.ActivateAsync()).ShouldBe(CarModeStartResult.NothingToTell);
        _car.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task Stop_ends_the_story_the_discovery_mode_and_the_car_screen()
    {
        _catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([Poi("Fort", Lat, Lon + EastMeters(5_000))]);
        await _car.ActivateAsync();

        await _car.StopAsync();

        _car.IsActive.ShouldBeFalse();
        _discovery.State.IsOn.ShouldBeFalse();
        _location.Started.ShouldBeFalse();
    }

    [Fact]
    public async Task The_car_screen_can_be_left_once_the_traveler_walks_again_and_discovery_keeps_going()
    {
        _catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([Poi("Fort", Lat, Lon + EastMeters(20_000))]);
        await _discovery.StartAsync(false);
        await DriveAsync(5);
        _car.Accept();
        _car.View.CanLeave.ShouldBeFalse("still driving");

        await WalkAsync(60, from: 5);
        _car.View.CanLeave.ShouldBeTrue();
        _car.Leave();

        _car.IsActive.ShouldBeFalse();
        _discovery.State.IsOn.ShouldBeTrue();
    }

    [Fact]
    public async Task Pause_and_next_drive_the_player()
    {
        _catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([Poi("Fort", Lat, Lon + EastMeters(20_000))]);
        await _car.ActivateAsync();
        await _audio.PlayNowAsync(new PlayRequest(Guid.NewGuid(), Guid.NewGuid(), "Un récit", [new AudioSource("https://m/a.mp3", AudioRole.Main)], PlayOrigin.Manual, 90, null));
        _car.View.Playing.ShouldBeTrue();
        _car.View.CurrentTitle.ShouldBe("Un récit");

        await _car.TogglePauseAsync();
        _car.View.Playing.ShouldBeFalse();

        await _car.NextAsync();
        _player.Calls.ShouldContain("stop");
    }

    private static PlayRequest Told(string title, PlayOrigin origin) =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, [new AudioSource("https://m/a.mp3", AudioRole.Main)], origin, 90, new Dictionary<string, double> { ["history"] = 1d });

    private async Task HearAsync(PlayRequest request)
    {
        await _audio.PlayNowAsync(request);
        _player.Raise(new AudioPlayerEvent.Ended());
    }

    [Fact]
    public async Task No_feedback_is_asked_while_driving_and_the_recap_is_offered_after_the_trip()
    {
        _catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([Poi("Fort", Lat, Lon + EastMeters(20_000))]);
        await _car.ActivateAsync();

        await HearAsync(Told("Première", PlayOrigin.Manual));
        await HearAsync(Told("Deuxième", PlayOrigin.Manual));

        _tracker.Banner.ShouldBeNull("F-10: no feedback banner while driving");
        _notifier.Bodies.ShouldBeEmpty("nor a notification");
        _tracker.Unrated.Count.ShouldBe(2, "the stories wait for the recap");

        _car.Leave();

        _tracker.RecapAvailable.ShouldBeTrue();
        _notifier.Bodies.ShouldHaveSingleItem().ShouldContain("2 histoires");
    }

    [Fact]
    public async Task Outside_car_mode_a_story_heard_on_request_still_asks_for_a_reaction()
    {
        await HearAsync(Told("Seule", PlayOrigin.Manual));

        _tracker.Banner.ShouldNotBeNull();
    }
}
