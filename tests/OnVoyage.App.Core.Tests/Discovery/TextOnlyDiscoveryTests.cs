#pragma warning disable xUnit1051 // driven against in-memory fakes
using System.Text.Json;
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

/// <summary>Stories published without audio (no TTS voice at the time) are announced by the discovery mode and read by the device voice.</summary>
public sealed class TextOnlyDiscoveryTests : IDisposable
{
    private const double Lat = 43.2965;
    private const double Lon = 5.3700;
    private const string Text = "Louis XIV fait bâtir le fort Saint-Jean à l'entrée du Vieux-Port.";

    private sealed class Settings(TriggerSettings current) : ITriggerSettingsProvider
    {
        public TriggerSettings Current => current;
    }

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero));
    private readonly ICatalogClient _catalog = Substitute.For<ICatalogClient>();
    private readonly SimulatedLocationSource _location = new();
    private readonly FakeAudioPlayer _player = new();
    private readonly RecordingNarrator _narrator = new();
    private readonly AudioPlaybackController _audio;
    private readonly List<DiscoveryModeController> _created = [];
    private readonly Guid _travelerId;
    private readonly InMemoryProfileStore _profiles = new();
    private readonly ISessionProvider _sessions = Substitute.For<ISessionProvider>();

    public TextOnlyDiscoveryTests()
    {
        LocalProfile profile = new();
        while (OnVoyage.Recommendation.Engine.ControlCohort.Contains(profile.TravelerId))
        {
            profile = new LocalProfile();
        }

        _travelerId = profile.TravelerId;
        _profiles.SaveAsync(profile, CancellationToken.None).GetAwaiter().GetResult();
        _sessions.EnsureSessionAsync(Arg.Any<CancellationToken>()).Returns(new AuthSessionDto("t", DateTimeOffset.MaxValue, "r", DateTimeOffset.MaxValue, _travelerId, true, null, []));
        _audio = new AudioPlaybackController(_player, new RecordingAnalytics(), new InMemoryFlagStore(), _clock, narrator: _narrator);
    }

    public void Dispose()
    {
        foreach (var controller in _created)
        {
            controller.Dispose();
        }
    }

    private DiscoveryModeController Discovery(TriggerSettings? settings = null, ITextNarrator? narrator = null)
    {
        var controller = new DiscoveryModeController(
            _catalog, _profiles, _sessions, _location, _audio, new InMemoryTellHistoryStore(), new RecordingVisits(), new RecordingKeepAwake(), new ManualCallMonitor(),
            new Settings(settings ?? new TriggerSettings()), new RecordingAnalytics(), _clock, narrator ?? _narrator);
        _created.Add(controller);
        return controller;
    }

    private static PoiSummaryDto TextOnly(string name, double latitude, double longitude) =>
        new(Guid.NewGuid(), name.ToLowerInvariant(), name, "history", latitude, longitude, 0.7, 0.8, 2, false, null, 90, new Dictionary<string, double> { ["history"] = 1d },
            Guid.NewGuid(), false, new Dictionary<string, string>());

    private void Catalog(PoiSummaryDto summary, string? text)
    {
        _catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([summary]);
        _catalog.GetPoiAsync(summary.Slug, Arg.Any<CancellationToken>()).Returns(new PoiDetailDto(
            summary.Id, summary.Slug, summary.Name, "history", summary.Latitude, summary.Longitude, 0.7, 2, false,
            [new StoryDto(summary.StoryId!.Value, "fr", "Le fort", text ?? string.Empty, 90, null, true)], []));
    }

    private LocationFix Fix(double seconds) => new(Lat, Lon, 8, 1.2, 90, _clock.GetUtcNow().AddSeconds(seconds));

    private async Task WalkAsync(DiscoveryModeController discovery, int fixes = 4)
    {
        for (var s = 0; s < fixes; s++)
        {
            _location.Emit(Fix(s));
            await discovery.LastHandling;
        }
    }

    [Fact]
    public async Task Walking_up_to_a_place_without_audio_plays_the_jingle_then_the_device_reads_the_text_fetched_from_the_catalog()
    {
        var fort = TextOnly("Fort", Lat + (40 / 111_320d), Lon);
        Catalog(fort, Text);
        var discovery = Discovery();
        (await discovery.StartAsync(false)).ShouldBe(DiscoveryStartResult.Started);

        await WalkAsync(discovery);

        _player.Calls.ShouldBe(["play:Jingle"]);
        _player.Raise(new AudioPlayerEvent.Ended());
        _narrator.Calls.ShouldBe([$"speak:fr:1:{Text}"]);
        _audio.State.Narrated.ShouldBeTrue();
        discovery.State.Engine.ShouldBe(EngineState.Playing);
        await _catalog.Received(1).GetPoiAsync("fort", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_voice_on_the_device_or_with_the_setting_off_such_places_leave_nothing_to_tell()
    {
        var fort = TextOnly("Fort", Lat, Lon);
        Catalog(fort, Text);

        (await Discovery(narrator: new NullTextNarrator()).StartAsync(false)).ShouldBe(DiscoveryStartResult.NothingToTell);
        (await Discovery(new TriggerSettings { AllowTextOnlyStories = false }).StartAsync(false)).ShouldBe(DiscoveryStartResult.NothingToTell);
        _location.Started.ShouldBeFalse();
    }

    [Fact]
    public async Task A_place_whose_text_is_not_there_is_not_announced_and_not_asked_for_again()
    {
        var fort = TextOnly("Fort", Lat + (40 / 111_320d), Lon);
        Catalog(fort, text: null); // a Premium story comes back without its text
        var discovery = Discovery();
        await discovery.StartAsync(false);

        await WalkAsync(discovery, fixes: 8);

        _player.Calls.ShouldBeEmpty();
        _narrator.Calls.ShouldBeEmpty();
        await _catalog.Received(1).GetPoiAsync("fort", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failing_catalog_does_not_break_the_mode()
    {
        var fort = TextOnly("Fort", Lat + (40 / 111_320d), Lon);
        _catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([fort]);
        _catalog.GetPoiAsync(fort.Slug, Arg.Any<CancellationToken>()).Returns<Task<PoiDetailDto?>>(_ => throw new HttpRequestException("offline"));
        var discovery = Discovery();
        await discovery.StartAsync(false);

        await WalkAsync(discovery);

        _player.Calls.ShouldBeEmpty();
        discovery.State.IsOn.ShouldBeTrue();
    }

    [Fact]
    public void Candidates_include_stories_without_audio_only_when_allowed()
    {
        var fort = TextOnly("Fort", Lat, Lon);
        var voiced = fort with { Id = Guid.NewGuid(), Slug = "voiced", StoryId = Guid.NewGuid(), AudioParts = new Dictionary<string, string> { ["main"] = "https://media/main.mp3" } };
        var profile = new LocalProfile();

        DiscoveryModeController.Build([fort, voiced], profile).Candidates.Select(c => c.PoiId).ShouldBe([voiced.Id]);
        var (candidates, stories) = DiscoveryModeController.Build([fort, voiced], profile, allowTextOnly: true);

        candidates.Select(c => c.PoiId).ShouldBe([fort.Id, voiced.Id], ignoreOrder: true);
        stories[fort.Id].HasMainAudio.ShouldBeFalse();
        stories[voiced.Id].HasMainAudio.ShouldBeTrue();
    }

    [Fact]
    public void The_remote_setting_text_only_stories_defaults_to_on_and_can_be_turned_off()
    {
        new TriggerSettings().AllowTextOnlyStories.ShouldBeTrue();
        TriggerSettings.FromJson(JsonDocument.Parse("{}").RootElement).AllowTextOnlyStories.ShouldBeTrue();
        TriggerSettings.FromJson(JsonDocument.Parse("""{"text_only_stories": false}""").RootElement).AllowTextOnlyStories.ShouldBeFalse();
        TriggerSettings.FromJson(JsonDocument.Parse("""{"text_only_stories": "no"}""").RootElement).AllowTextOnlyStories.ShouldBeTrue();
    }
}
