#pragma warning disable xUnit1051 // driven against in-memory fakes
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.Core.Profile;
using OnVoyage.App.Core.Tests.Audio;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.App.Core.Tests.Discovery;

internal sealed class RecordingKeepAwake : IScreenKeepAwake
{
    public List<bool> Calls { get; } = [];

    public void SetAwake(bool awake) => Calls.Add(awake);
}

internal sealed class RecordingVisits : IVisitSink
{
    public List<Visit> Visits { get; } = [];

    public Task RecordAsync(Visit visit, CancellationToken cancellationToken)
    {
        Visits.Add(visit);
        return Task.CompletedTask;
    }
}

internal sealed class ManualCallMonitor : ICallMonitor
{
    public event Action<bool>? CallStateChanged;

    public void Set(bool inCall) => CallStateChanged?.Invoke(inCall);
}

public sealed class DiscoveryModeControllerTests : IDisposable
{
    private const double Lat = 43.2965;
    private const double Lon = 5.3700;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero));
    private readonly ICatalogClient _catalog = Substitute.For<ICatalogClient>();
    private readonly InMemoryProfileStore _profiles = new();
    private readonly ISessionProvider _sessions = Substitute.For<ISessionProvider>();
    private readonly SimulatedLocationSource _location = new();
    private readonly FakeAudioPlayer _player = new();
    private readonly RecordingAnalytics _analytics = new();
    private readonly InMemoryTellHistoryStore _history = new();
    private readonly RecordingVisits _visits = new();
    private readonly RecordingKeepAwake _awake = new();
    private readonly ManualCallMonitor _calls = new();
    private readonly AudioPlaybackController _audio;
    private readonly DiscoveryModeController _discovery;
    private readonly Guid _travelerId;

    public DiscoveryModeControllerTests()
    {
        LocalProfile profile = new();
        while (OnVoyage.Recommendation.Engine.ControlCohort.Contains(profile.TravelerId))
        {
            profile = new LocalProfile();
        }

        _travelerId = profile.TravelerId;
        _profiles.SaveAsync(profile, CancellationToken.None).GetAwaiter().GetResult();
        _sessions.EnsureSessionAsync(Arg.Any<CancellationToken>()).Returns(new AuthSessionDto("t", DateTimeOffset.MaxValue, "r", DateTimeOffset.MaxValue, _travelerId, true, null, []));
        _audio = new AudioPlaybackController(_player, _analytics, new InMemoryFlagStore(), _clock);
        _discovery = new DiscoveryModeController(_catalog, _profiles, _sessions, _location, _audio, _history, _visits, _awake, _calls, new DefaultTriggerSettingsProvider(), _analytics, _clock);
    }

    public void Dispose() => _discovery.Dispose();

    private static PoiSummaryDto Poi(string name, double latitude, double longitude, double importance = 0.7, bool story = true, bool fragile = false) =>
        new(Guid.NewGuid(), name.ToLowerInvariant(), name, "history", latitude, longitude, importance, 0.8, 2, false, null, 90, new Dictionary<string, double> { ["history"] = 1d },
            story ? Guid.NewGuid() : null, fragile,
            story ? new Dictionary<string, string> { ["main"] = $"https://media/{name}/main.mp3", ["announce_front"] = $"https://media/{name}/front.mp3", ["announce_left"] = $"https://media/{name}/left.mp3", ["announce_right"] = $"https://media/{name}/right.mp3" } : null);

    private void Catalog(params PoiSummaryDto[] pois) =>
        _catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns(pois);

    private LocationFix Fix(double seconds, double latitude = Lat, double longitude = Lon, double speed = 1.2, double heading = 90) =>
        new(latitude, longitude, 8, speed, heading, _clock.GetUtcNow().AddSeconds(seconds));

    private async Task EmitAsync(LocationFix fix)
    {
        _location.Emit(fix);
        await _discovery.LastHandling;
    }

    /// <summary>Four fixes in the same spot, enough for the engine to know the mode.</summary>
    private async Task StandAsync(double fromSecond = 0, double latitude = Lat, double longitude = Lon)
    {
        for (var s = 0; s < 4; s++)
        {
            await EmitAsync(Fix(fromSecond + s, latitude, longitude));
        }
    }

    [Fact]
    public async Task Starting_asks_for_the_location_keeps_the_screen_on_and_reports_the_state()
    {
        Catalog(Poi("Fort", Lat, Lon));

        var result = await _discovery.StartAsync(keepScreenOn: true);

        result.ShouldBe(DiscoveryStartResult.Started);
        _location.Started.ShouldBeTrue();
        _awake.Calls.ShouldBe([true]);
        _discovery.State.IsOn.ShouldBeTrue();
        _discovery.State.Engine.ShouldBe(EngineState.Listening);
        (await _discovery.StartAsync(true)).ShouldBe(DiscoveryStartResult.AlreadyOn);
    }

    [Fact]
    public async Task A_refused_permission_leaves_discovery_off_and_the_screen_alone()
    {
        Catalog(Poi("Fort", Lat, Lon));
        _location.Permitted = false;

        (await _discovery.StartAsync(true)).ShouldBe(DiscoveryStartResult.PermissionDenied);

        _discovery.State.IsOn.ShouldBeFalse();
        _awake.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Places_without_a_voiced_story_leave_nothing_to_tell()
    {
        Catalog(Poi("Muet", Lat, Lon, story: false));

        (await _discovery.StartAsync(true)).ShouldBe(DiscoveryStartResult.NothingToTell);

        _location.Started.ShouldBeFalse("the permission is not even asked when there is nothing to tell");
    }

    [Fact]
    public async Task Walking_up_to_a_place_plays_the_jingle_the_direction_then_the_story_and_emits_an_event_without_coordinates()
    {
        var fort = Poi("Fort", Lat + (40 / 111_320d), Lon);
        Catalog(fort);
        await _discovery.StartAsync(false);

        await StandAsync();

        _player.Played.Select(item => item.Source.Role).ShouldBe([AudioRole.Jingle]);
        _discovery.State.Engine.ShouldBe(EngineState.Announcing);
        _discovery.State.LastTitle.ShouldBe("Fort");
    }

    [Fact]
    public async Task The_announcement_names_the_side_and_the_event_carries_the_place_the_mode_and_a_rounded_distance_only()
    {
        var fort = Poi("Fort", Lat + (40 / 111_320d), Lon);
        Catalog(fort);
        await _discovery.StartAsync(false);

        await StandAsync();
        _player.Raise(new AudioPlayerEvent.Ended());

        _player.Played[1].Source.Uri.ShouldEndWith("left.mp3");
        var triggered = _analytics.Events.Single(item => item.Name == "story_triggered").Properties;
        triggered.Keys.ShouldBe(["poi_id", "mode", "distance_m"], ignoreOrder: true);
        triggered["poi_id"].ShouldBe(fort.Id.ToString());
        triggered["mode"].ShouldBe("walk");
        ((int)triggered["distance_m"]!).ShouldBe(50);
    }

    [Fact]
    public async Task The_engine_follows_the_player_and_the_place_is_remembered_on_the_device()
    {
        Catalog(Poi("Fort", Lat, Lon), Poi("Autre", Lat + (30 / 111_320d), Lon));
        await _discovery.StartAsync(false);
        await StandAsync();
        _player.Raise(new AudioPlayerEvent.Ended()); // jingle
        _player.Raise(new AudioPlayerEvent.Ended()); // direction

        _discovery.State.Engine.ShouldBe(EngineState.Playing);
        (await _history.LoadAsync(CancellationToken.None)).Count.ShouldBe(1);

        _player.At(90, 90);
        _player.Raise(new AudioPlayerEvent.Ended()); // the story
        _discovery.State.Engine.ShouldBe(EngineState.Cooldown);

        // The second place waits for the 90 s gap, then is told; the first never is again.
        _clock.Advance(TimeSpan.FromSeconds(30));
        await EmitAsync(Fix(60));
        _player.Played.Count(item => item.Source.Role == AudioRole.Main).ShouldBe(1);
        _clock.Advance(TimeSpan.FromSeconds(100));
        await EmitAsync(Fix(160));
        _player.Played.Count(item => item.Source.Role == AudioRole.Jingle).ShouldBe(2);
        _player.Played.Where(item => item.Source.Role == AudioRole.Main).Select(item => item.Source.Uri).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Nothing_is_told_over_a_story_the_traveler_started_by_hand()
    {
        Catalog(Poi("Fort", Lat, Lon));
        await _discovery.StartAsync(false);
        await _audio.PlayNowAsync(new PlayRequest(Guid.NewGuid(), Guid.NewGuid(), "Choisie", [new AudioSource("https://media/x.mp3", AudioRole.Main)], PlayOrigin.Manual));

        await StandAsync();

        _player.Played.Select(item => item.Title).ShouldBe(["Choisie"]);
        _analytics.Names.ShouldNotContain("story_triggered");
    }

    [Fact]
    public async Task Nothing_is_told_during_a_phone_call()
    {
        Catalog(Poi("Fort", Lat, Lon));
        await _discovery.StartAsync(false);
        _calls.Set(true);

        await StandAsync();
        _player.Played.ShouldBeEmpty();

        _calls.Set(false);
        await StandAsync(fromSecond: 10);
        _player.Played.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Fragile_places_are_never_told()
    {
        Catalog(Poi("Calanque", Lat, Lon, fragile: true));
        await _discovery.StartAsync(false);

        await StandAsync();

        _player.Played.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_stay_at_a_place_already_told_becomes_a_visit_without_coordinates()
    {
        var fort = Poi("Fort", Lat, Lon);
        Catalog(fort);
        await _history.MarkAsync(fort.Id, _clock.GetUtcNow().AddDays(-1), CancellationToken.None);
        await _discovery.StartAsync(false);

        for (var s = 0; s <= 8 * 60; s += 10)
        {
            await EmitAsync(Fix(s, speed: 0));
        }

        await _discovery.StopAsync();

        var visit = _visits.Visits.ShouldHaveSingleItem();
        visit.PoiId.ShouldBe(fort.Id);
        visit.Dwell.TotalMinutes.ShouldBeGreaterThan(7);
    }

    [Fact]
    public async Task Stopping_stops_listening_releases_the_screen_and_ignores_later_positions()
    {
        Catalog(Poi("Fort", Lat, Lon));
        await _discovery.StartAsync(true);

        await _discovery.StopAsync();
        _location.Emit(Fix(0));
        await _discovery.LastHandling;

        _location.Started.ShouldBeFalse();
        _awake.Calls.ShouldBe([true, false]);
        _discovery.State.ShouldBe(DiscoveryState.Off);
        _player.Played.ShouldBeEmpty();
    }

    [Fact]
    public async Task Discovery_switches_itself_off_after_two_idle_hours()
    {
        Catalog(Poi("Loin", Lat + 0.5, Lon));
        await _discovery.StartAsync(true);
        await EmitAsync(Fix(0, speed: 0));

        _clock.Advance(TimeSpan.FromMinutes(90));
        _discovery.State.IsOn.ShouldBeTrue();
        _clock.Advance(TimeSpan.FromMinutes(40));

        (await Eventually(() => !_discovery.State.IsOn)).ShouldBeTrue("auto stop after 2 hours without moving");
        _awake.Calls.Last().ShouldBeFalse();
    }

    [Fact]
    public async Task A_lost_signal_is_shown_in_the_state()
    {
        Catalog(Poi("Loin", Lat + 0.5, Lon));
        await _discovery.StartAsync(false);
        await EmitAsync(Fix(0));

        _clock.Advance(TimeSpan.FromSeconds(30));

        _discovery.State.SignalLost.ShouldBeTrue();
    }

    [Fact]
    public void Candidates_come_from_places_with_a_voiced_story_and_take_the_personal_score_unless_in_the_control_cohort()
    {
        var voiced = Poi("Fort", Lat, Lon, importance: 0.6);
        var muted = Poi("Muet", Lat, Lon, story: false);
        LocalProfile personal = new() { Affinities = new Dictionary<string, double> { ["history"] = 0.9 }, Depth = 10 };
        while (OnVoyage.Recommendation.Engine.ControlCohort.Contains(personal.TravelerId))
        {
            personal = personal with { TravelerId = Guid.NewGuid() };
        }

        var (candidates, stories) = DiscoveryModeController.Build([voiced, muted], personal);

        var candidate = candidates.ShouldHaveSingleItem();
        candidate.Importance.ShouldBe(60);
        candidate.BaseScore.ShouldBeInRange(0, 1);
        stories.ShouldContainKey(voiced.Id);

        LocalProfile control = new();
        while (!OnVoyage.Recommendation.Engine.ControlCohort.Contains(control.TravelerId))
        {
            control = control with { TravelerId = Guid.NewGuid() };
        }

        DiscoveryModeController.Build([voiced], control).Candidates.Single().BaseScore.ShouldBe(0.6, 0.001);
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }
}
