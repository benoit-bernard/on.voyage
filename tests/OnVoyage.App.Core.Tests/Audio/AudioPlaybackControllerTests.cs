#pragma warning disable xUnit1051 // the controller is driven against in-memory fakes: nothing here waits long enough to need cancelling
using Microsoft.Extensions.Time.Testing;
using OnVoyage.App.Core.Audio;

namespace OnVoyage.App.Core.Tests.Audio;

internal sealed class FakeAudioPlayer : IAudioPlayer
{
    public event Action<AudioPlayerEvent>? Event;

    public List<string> Calls { get; } = [];

    public List<(AudioSource Source, double Speed, string Title)> Played { get; } = [];

    public Exception? FailWith { get; set; }

    public Task PlayAsync(AudioSource source, double speed, string title, CancellationToken cancellationToken)
    {
        if (FailWith is { } failure)
        {
            throw failure;
        }

        Calls.Add($"play:{source.Role}");
        Played.Add((source, speed, title));
        return Task.CompletedTask;
    }

    public Task PauseAsync() => Record("pause");

    public Task ResumeAsync() => Record("resume");

    public Task SeekAsync(TimeSpan position) => Record($"seek:{position.TotalSeconds:0}");

    public Task SetSpeedAsync(double speed) => Record($"speed:{speed}");

    public Task StopAsync() => Record("stop");

    public void Raise(AudioPlayerEvent playerEvent) => Event?.Invoke(playerEvent);

    public void At(double seconds, double durationSeconds) => Raise(new AudioPlayerEvent.PositionChanged(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(durationSeconds)));

    private Task Record(string call)
    {
        Calls.Add(call);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingAnalytics : IAnalyticsSink
{
    public List<(string Name, IReadOnlyDictionary<string, object?> Properties)> Events { get; } = [];

    public void Track(string name, IReadOnlyDictionary<string, object?> properties) => Events.Add((name, properties));

    public IEnumerable<string> Names => Events.Select(item => item.Name);
}

public sealed class AudioPlaybackControllerTests
{
    private readonly FakeAudioPlayer _player = new();
    private readonly RecordingAnalytics _analytics = new();
    private readonly InMemoryFlagStore _flags = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero));
    private readonly AudioPlaybackController _controller;

    public AudioPlaybackControllerTests() => _controller = new AudioPlaybackController(_player, _analytics, _flags, _clock);

    private static PlayRequest Story(string title = "Fort Saint-Jean", PlayOrigin origin = PlayOrigin.Manual, bool announced = false, Guid? id = null)
    {
        List<AudioSource> parts = [];
        if (announced)
        {
            parts.Add(new AudioSource("asset://jingle", AudioRole.Jingle));
            parts.Add(new AudioSource("https://media/announce_front.mp3", AudioRole.Announcement));
        }

        parts.Add(new AudioSource("https://media/main.mp3", AudioRole.Main));
        return new PlayRequest(id ?? Guid.NewGuid(), Guid.NewGuid(), title, parts, origin);
    }

    [Fact]
    public async Task A_discovery_story_plays_the_jingle_then_the_direction_then_the_story_and_only_the_story_is_counted()
    {
        List<string> mainStarted = [];
        _controller.MainStarted += request => mainStarted.Add(request.Title);

        await _controller.PlayNowAsync(Story(announced: true, origin: PlayOrigin.Discovery));
        _controller.State.Current!.Role.ShouldBe(AudioRole.Jingle);
        _analytics.Names.ShouldBeEmpty();

        _player.Raise(new AudioPlayerEvent.Ended());
        _controller.State.Current!.Role.ShouldBe(AudioRole.Announcement);
        _player.Raise(new AudioPlayerEvent.Ended());
        _controller.State.Current!.Role.ShouldBe(AudioRole.Main);

        _player.Calls.ShouldBe(["play:Jingle", "play:Announcement", "play:Main"]);
        mainStarted.ShouldBe(["Fort Saint-Jean"]);
        _analytics.Names.ShouldBe(["audio_started"]);
        _analytics.Events[0].Properties["origin"].ShouldBe("discovery");
    }

    [Fact]
    public async Task Stopping_at_83_percent_reports_the_75_percent_milestone_and_a_skip_at_83()
    {
        await _controller.PlayNowAsync(Story());

        _player.At(10, 100);
        _player.At(30, 100);
        _player.At(60, 100);
        _player.At(80, 100);
        _player.At(83, 100);
        await _controller.StopAsync();

        _analytics.Events.Where(item => item.Name == "audio_progress").Select(item => item.Properties["percent"]).ShouldBe([25, 50, 75]);
        var skipped = _analytics.Events.Single(item => item.Name == "audio_skipped");
        skipped.Properties["percent"].ShouldBe(83);
        _analytics.Names.ShouldNotContain("audio_completed");
    }

    [Fact]
    public async Task Listening_to_95_percent_completes_the_story_and_ending_it_afterwards_is_not_a_skip()
    {
        List<(string Title, bool Completed)> ended = [];
        _controller.StoryEnded += (request, completed) => ended.Add((request.Title, completed));
        await _controller.PlayNowAsync(Story());

        _player.At(96, 100);
        _player.Raise(new AudioPlayerEvent.Ended());

        _analytics.Names.Count(name => name == "audio_completed").ShouldBe(1);
        _analytics.Names.ShouldNotContain("audio_skipped");
        ended.ShouldBe([("Fort Saint-Jean", true)]);
        _controller.State.Phase.ShouldBe(PlaybackPhase.Idle);
    }

    [Fact]
    public async Task A_story_that_ends_without_a_late_position_event_still_counts_as_completed()
    {
        await _controller.PlayNowAsync(Story());

        _player.At(50, 100);
        _player.Raise(new AudioPlayerEvent.Ended());

        _analytics.Names.ShouldContain("audio_completed");
    }

    [Fact]
    public async Task One_story_plays_and_one_waits_a_newer_waiting_one_replaces_the_older_and_the_waiting_one_follows()
    {
        await _controller.EnqueueAsync(Story("Premier"));
        await _controller.EnqueueAsync(Story("Deuxième"));
        await _controller.EnqueueAsync(Story("Troisième"));

        _controller.State.Current!.Title.ShouldBe("Premier");
        _controller.State.WaitingTitle.ShouldBe("Troisième");

        _player.Raise(new AudioPlayerEvent.Ended());

        _controller.State.Current!.Title.ShouldBe("Troisième");
        _controller.State.WaitingTitle.ShouldBeNull();
    }

    [Fact]
    public async Task Playing_now_replaces_the_current_story_and_clears_the_queue()
    {
        await _controller.EnqueueAsync(Story("Premier"));
        await _controller.EnqueueAsync(Story("En attente"));
        _player.At(40, 100);

        await _controller.PlayNowAsync(Story("Choisie"));

        _controller.State.Current!.Title.ShouldBe("Choisie");
        _controller.State.WaitingTitle.ShouldBeNull();
        _analytics.Events.Single(item => item.Name == "audio_skipped").Properties["percent"].ShouldBe(40);
    }

    [Fact]
    public async Task Stop_means_stop_and_next_moves_on()
    {
        await _controller.EnqueueAsync(Story("Premier"));
        await _controller.EnqueueAsync(Story("Suivant"));
        await _controller.NextAsync();
        _controller.State.Current!.Title.ShouldBe("Suivant");

        await _controller.EnqueueAsync(Story("Encore"));
        await _controller.StopAsync();

        _controller.State.Phase.ShouldBe(PlaybackPhase.Idle);
        _controller.State.WaitingTitle.ShouldBeNull();
        _player.Calls.Last().ShouldBe("stop");
    }

    [Fact]
    public async Task Speeds_are_one_one_and_a_quarter_and_one_and_a_half_and_apply_to_what_plays_next()
    {
        await _controller.PlayNowAsync(Story());

        await _controller.SetSpeedAsync(2);
        _controller.State.Speed.ShouldBe(1);
        await _controller.CycleSpeedAsync();
        _controller.State.Speed.ShouldBe(1.25);
        await _controller.CycleSpeedAsync();
        _controller.State.Speed.ShouldBe(1.5);
        await _controller.CycleSpeedAsync();
        _controller.State.Speed.ShouldBe(1);
        await _controller.SetSpeedAsync(1.5);

        _analytics.Events.Where(item => item.Name == "audio_speed_changed").Select(item => item.Properties["speed"]).ShouldBe([1.25, 1.5, 1d, 1.5]);
        await _controller.EnqueueAsync(Story("Suivante"));
        _player.Raise(new AudioPlayerEvent.Ended());
        _player.Played.Last().Speed.ShouldBe(1.5);
    }

    [Fact]
    public async Task Ten_seconds_back_and_forward_stay_inside_the_story()
    {
        await _controller.PlayNowAsync(Story());
        _player.At(5, 90);

        await _controller.SkipAsync(-10);
        await _controller.SkipAsync(10);
        _player.At(85, 90);
        await _controller.SkipAsync(10);

        _player.Calls.Where(call => call.StartsWith("seek", StringComparison.Ordinal)).ShouldBe(["seek:0", "seek:10", "seek:90"]);
    }

    [Fact]
    public async Task A_call_pauses_and_the_story_resumes_if_it_lasts_less_than_30_seconds()
    {
        await _controller.PlayNowAsync(Story());

        _player.Raise(new AudioPlayerEvent.Interruption(Began: true));
        _controller.State.Phase.ShouldBe(PlaybackPhase.Paused);
        _clock.Advance(TimeSpan.FromSeconds(20));
        _player.Raise(new AudioPlayerEvent.Interruption(Began: false));

        _controller.State.Phase.ShouldBe(PlaybackPhase.Playing);
        _player.Calls.TakeLast(2).ShouldBe(["pause", "resume"]);
    }

    [Fact]
    public async Task A_longer_interruption_leaves_the_story_paused()
    {
        await _controller.PlayNowAsync(Story());

        _player.Raise(new AudioPlayerEvent.Interruption(Began: true));
        _clock.Advance(TimeSpan.FromSeconds(45));
        _player.Raise(new AudioPlayerEvent.Interruption(Began: false));

        _controller.State.Phase.ShouldBe(PlaybackPhase.Paused);
    }

    [Fact]
    public async Task A_story_the_traveler_had_paused_is_not_resumed_by_the_end_of_a_call()
    {
        await _controller.PlayNowAsync(Story());
        await _controller.PauseAsync();

        _player.Raise(new AudioPlayerEvent.Interruption(Began: true));
        _clock.Advance(TimeSpan.FromSeconds(5));
        _player.Raise(new AudioPlayerEvent.Interruption(Began: false));

        _controller.State.Phase.ShouldBe(PlaybackPhase.Paused);
    }

    [Fact]
    public async Task The_synthetic_voice_notice_shows_once_per_installation()
    {
        await _controller.PlayNowAsync(Story());
        _controller.State.ShowAiNotice.ShouldBeTrue();

        await _controller.AcknowledgeAiNoticeAsync();
        _controller.State.ShowAiNotice.ShouldBeFalse();
        (await _flags.GetAsync(AudioPlaybackController.AiNoticeFlag, CancellationToken.None)).ShouldBeTrue();

        await _controller.PlayNowAsync(Story("Autre"));
        _controller.State.ShowAiNotice.ShouldBeFalse();

        var afterRestart = new AudioPlaybackController(new FakeAudioPlayer(), _analytics, _flags, _clock);
        await afterRestart.PlayNowAsync(Story());
        afterRestart.State.ShowAiNotice.ShouldBeFalse();
        AudioSettings.VoiceNotice.ShouldBe("Voix générée par intelligence artificielle");
    }

    [Fact]
    public async Task Starting_over_or_playing_the_same_story_again_is_a_replay()
    {
        var id = Guid.NewGuid();
        await _controller.PlayNowAsync(Story(id: id));
        _player.At(40, 100);

        await _controller.ReplayAsync();
        await _controller.PlayNowAsync(Story(id: id));

        _analytics.Names.Count(name => name == "audio_replayed").ShouldBe(2);
        _player.Calls.ShouldContain("seek:0");
    }

    [Fact]
    public async Task A_failing_player_ends_the_story_with_a_message_and_lets_the_waiting_one_play()
    {
        List<bool> ended = [];
        _controller.StoryEnded += (_, completed) => ended.Add(completed);
        await _controller.PlayNowAsync(Story("Premier"));
        await _controller.EnqueueAsync(Story("Suivant"));

        _player.Raise(new AudioPlayerEvent.Failed("network"));

        ended.ShouldBe([false]);
        _controller.State.Current!.Title.ShouldBe("Suivant");

        _player.FailWith = new InvalidOperationException("no codec");
        await _controller.PlayNowAsync(Story("Cassée"));
        _controller.State.Phase.ShouldBe(PlaybackPhase.Idle);
        _controller.State.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_request_without_a_story_part_is_refused()
    {
        await _controller.PlayNowAsync(new PlayRequest(Guid.NewGuid(), Guid.NewGuid(), "Vide", [new AudioSource("asset://jingle", AudioRole.Jingle)], PlayOrigin.Manual));

        _controller.State.Phase.ShouldBe(PlaybackPhase.Idle);
        _controller.State.Error.ShouldNotBeNull();
        _player.Played.ShouldBeEmpty();
    }

    [Fact]
    public async Task Listening_events_carry_ids_and_never_coordinates()
    {
        await _controller.PlayNowAsync(Story());
        _player.At(30, 100);
        await _controller.StopAsync();

        _analytics.Events.SelectMany(item => item.Properties.Keys).Distinct().ShouldBeSubsetOf(["story_id", "poi_id", "origin", "percent", "speed"]);
    }

    [Fact]
    public async Task The_state_is_published_on_every_change()
    {
        var changes = 0;
        _controller.Changed += () => changes++;

        await _controller.PlayNowAsync(Story());
        _player.At(10, 100);
        await _controller.PauseAsync();

        changes.ShouldBeGreaterThanOrEqualTo(4);
        _controller.State.Current!.Position.ShouldBe(TimeSpan.FromSeconds(10));
        AudioPlaybackController.FormatTime(TimeSpan.FromSeconds(94)).ShouldBe("01:34");
    }
}
