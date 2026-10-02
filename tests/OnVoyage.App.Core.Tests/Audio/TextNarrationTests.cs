#pragma warning disable xUnit1051 // driven against in-memory fakes: nothing here waits long enough to need cancelling
using Microsoft.Extensions.Time.Testing;
using OnVoyage.App.Core.Audio;

namespace OnVoyage.App.Core.Tests.Audio;

/// <summary>A speech engine whose pieces end when the test says so.</summary>
internal sealed class FakeSpeechEngine : ISpeechEngine
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsAvailable { get; set; } = true;

    public bool SupportsRate { get; set; } = true;

    public List<(string Text, string Language, double Rate)> Spoken { get; } = [];

    public int Cancelled { get; private set; }

    public Exception? FailWith { get; set; }

    public async Task<bool> SpeakAsync(string text, string language, double rate, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> mine;
        lock (_gate)
        {
            Spoken.Add((text, language, rate));
            if (FailWith is { } failure)
            {
                throw failure;
            }

            mine = _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        await using var registration = cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                Cancelled++;
            }

            mine.TrySetResult(false);
        });
        return await mine.Task;
    }

    /// <summary>The piece being read ends normally.</summary>
    public void Finish()
    {
        TaskCompletionSource<bool> pending;
        lock (_gate)
        {
            pending = _pending;
        }

        pending.TrySetResult(true);
    }

    public async Task WaitForSpokenAsync(int count)
    {
        for (var i = 0; i < 400; i++)
        {
            lock (_gate)
            {
                if (Spoken.Count >= count)
                {
                    return;
                }
            }

            await Task.Delay(5);
        }

        throw new TimeoutException($"Only {Spoken.Count} pieces were spoken, {count} expected.");
    }
}

internal sealed class RecordingNarrator : ITextNarrator
{
    public event Action<NarrationEvent>? Event;

    public bool IsAvailable { get; set; } = true;

    public bool SupportsSpeed { get; set; } = true;

    public List<string> Calls { get; } = [];

    public Task SpeakAsync(string text, string language, double speed, CancellationToken cancellationToken) => Record($"speak:{language}:{speed}:{text}");

    public Task PauseAsync() => Record("pause");

    public Task ResumeAsync() => Record("resume");

    public Task SetSpeedAsync(double speed) => Record($"speed:{speed}");

    public Task StopAsync() => Record("stop");

    public void Raise(NarrationEvent narration) => Event?.Invoke(narration);

    private Task Record(string call)
    {
        Calls.Add(call);
        return Task.CompletedTask;
    }
}

public sealed class NarrationChunkerTests
{
    [Fact]
    public void Short_text_is_one_piece_and_blank_text_is_none()
    {
        NarrationChunker.Split("Un fort. Deux tours.").ShouldBe(["Un fort. Deux tours."]);
        NarrationChunker.Split("  \n ").ShouldBeEmpty();
    }

    [Fact]
    public void Pieces_end_on_sentence_ends_and_never_exceed_the_limit()
    {
        var text = string.Join(' ', Enumerable.Range(1, 40).Select(i => $"Voici la phrase numéro {i} de l'histoire."));

        var pieces = NarrationChunker.Split(text, 120);

        pieces.Count.ShouldBeGreaterThan(5);
        pieces.ShouldAllBe(piece => piece.Length <= 120);
        pieces.ShouldAllBe(piece => piece.EndsWith('.'));
        string.Join(' ', pieces).ShouldBe(text);
    }

    [Fact]
    public void A_sentence_longer_than_the_limit_is_cut_at_a_comma_then_a_space_and_loses_no_word()
    {
        var sentence = string.Join(", ", Enumerable.Range(1, 30).Select(i => $"mot{i}")) + " fin.";

        var pieces = NarrationChunker.Split(sentence, 60);

        pieces.ShouldAllBe(piece => piece.Length <= 60);
        string.Join(' ', pieces).Replace(",", string.Empty, StringComparison.Ordinal).Split(' ').ShouldBe(sentence.Replace(",", string.Empty, StringComparison.Ordinal).Split(' '));
    }

    [Fact]
    public void Paragraph_breaks_separate_sentences_even_without_a_full_stop()
    {
        NarrationChunker.Split("Titre sans point\n\nPremière phrase.", 20).ShouldBe(["Titre sans point", "Première phrase."]);
    }
}

public sealed class SequentialTextNarratorTests
{
    private readonly FakeSpeechEngine _engine = new();
    private readonly SequentialTextNarrator _narrator;
    private readonly List<NarrationEvent> _events = [];

    public SequentialTextNarratorTests()
    {
        _narrator = new SequentialTextNarrator(_engine);
        _narrator.Event += item =>
        {
            lock (_events)
            {
                _events.Add(item);
            }
        };
    }

    private static string Story => string.Join(' ', Enumerable.Range(1, 6).Select(i => $"{new string('x', 70)} {i}."));

    private NarrationEvent[] Events()
    {
        lock (_events)
        {
            return [.. _events];
        }
    }

    private async Task WaitForEventsAsync(int count)
    {
        for (var i = 0; i < 400 && Events().Length < count; i++)
        {
            await Task.Delay(5);
        }

        Events().Length.ShouldBeGreaterThanOrEqualTo(count);
    }

    [Fact]
    public async Task It_reads_the_pieces_in_order_in_the_language_and_reports_progress_then_the_end()
    {
        await _narrator.SpeakAsync("Une phrase courte. Une autre phrase courte.", "fr", 1.25, CancellationToken.None);
        await _engine.WaitForSpokenAsync(1);

        _engine.Spoken.ShouldBe([("Une phrase courte. Une autre phrase courte.", "fr", 1.25)]);
        _engine.Finish();
        await WaitForEventsAsync(2);

        Events()[0].ShouldBeOfType<NarrationEvent.Progress>().Fraction.ShouldBe(1d);
        Events()[1].ShouldBeOfType<NarrationEvent.Ended>();
    }

    [Fact]
    public async Task Progress_is_measured_in_text_across_pieces()
    {
        await _narrator.SpeakAsync(Story, "fr", 1d, CancellationToken.None);
        var pieces = NarrationChunker.Split(Story).Count;
        pieces.ShouldBeGreaterThan(1);

        await _engine.WaitForSpokenAsync(1);
        _engine.Finish();
        await WaitForEventsAsync(1);

        var fraction = Events()[0].ShouldBeOfType<NarrationEvent.Progress>().Fraction;
        fraction.ShouldBeGreaterThan(0d);
        fraction.ShouldBeLessThan(1d);
    }

    [Fact]
    public async Task Pause_stops_the_piece_and_resume_reads_that_same_piece_again()
    {
        await _narrator.SpeakAsync(Story, "fr", 1d, CancellationToken.None);
        await _engine.WaitForSpokenAsync(1);
        var first = _engine.Spoken[0].Text;

        await _narrator.PauseAsync();
        _engine.Cancelled.ShouldBe(1);
        await Task.Delay(30);
        _engine.Spoken.Count.ShouldBe(1); // nothing is read while paused

        await _narrator.ResumeAsync();
        await _engine.WaitForSpokenAsync(2);

        _engine.Spoken[1].Text.ShouldBe(first);
        Events().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_speed_change_restarts_the_current_piece_at_the_new_rate_when_the_engine_can_and_is_ignored_otherwise()
    {
        await _narrator.SpeakAsync(Story, "fr", 1d, CancellationToken.None);
        await _engine.WaitForSpokenAsync(1);

        await _narrator.SetSpeedAsync(1.5);
        await _engine.WaitForSpokenAsync(2);

        _engine.Spoken[1].Rate.ShouldBe(1.5);
        _engine.Spoken[1].Text.ShouldBe(_engine.Spoken[0].Text);

        var fixedRate = new FakeSpeechEngine { SupportsRate = false };
        var plain = new SequentialTextNarrator(fixedRate);
        plain.SupportsSpeed.ShouldBeFalse();
        await plain.SpeakAsync(Story, "fr", 1d, CancellationToken.None);
        await fixedRate.WaitForSpokenAsync(1);
        await plain.SetSpeedAsync(1.5);
        await Task.Delay(30);
        fixedRate.Spoken.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Stop_ends_everything_without_an_event_and_a_new_story_replaces_the_old_one()
    {
        await _narrator.SpeakAsync(Story, "fr", 1d, CancellationToken.None);
        await _engine.WaitForSpokenAsync(1);
        await _narrator.StopAsync();
        _engine.Finish(); // a late "ended" from the engine after the stop must change nothing
        await Task.Delay(30);
        Events().ShouldBeEmpty();

        await _narrator.SpeakAsync("Autre histoire.", "fr", 1d, CancellationToken.None);
        await _engine.WaitForSpokenAsync(2);
        _engine.Spoken[1].Text.ShouldBe("Autre histoire.");
    }

    [Fact]
    public async Task An_engine_failure_is_reported_once_and_empty_text_ends_at_once()
    {
        _engine.FailWith = new InvalidOperationException("no voice");
        await _narrator.SpeakAsync("Du texte.", "fr", 1d, CancellationToken.None);
        await WaitForEventsAsync(1);
        Events().ShouldHaveSingleItem().ShouldBeOfType<NarrationEvent.Failed>().Message.ShouldBe("no voice");

        var other = new SequentialTextNarrator(new FakeSpeechEngine());
        List<NarrationEvent> seen = [];
        other.Event += seen.Add;
        await other.SpeakAsync("   ", "fr", 1d, CancellationToken.None);
        seen.ShouldHaveSingleItem().ShouldBeOfType<NarrationEvent.Ended>();
    }

    [Fact]
    public void Availability_follows_the_engine_and_the_null_narrator_is_never_available()
    {
        new SequentialTextNarrator(new FakeSpeechEngine { IsAvailable = false }).IsAvailable.ShouldBeFalse();
        _narrator.IsAvailable.ShouldBeTrue();
        new NullTextNarrator().IsAvailable.ShouldBeFalse();
    }
}

public sealed class NarratedPlaybackTests
{
    private readonly FakeAudioPlayer _player = new();
    private readonly RecordingNarrator _narrator = new();
    private readonly RecordingAnalytics _analytics = new();
    private readonly AudioPlaybackController _controller;

    public NarratedPlaybackTests() =>
        _controller = new AudioPlaybackController(_player, _analytics, new InMemoryFlagStore(), new FakeTimeProvider(new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero)), narrator: _narrator);

    private static PlayRequest TextOnly(IReadOnlyList<AudioSource>? parts = null, int? seconds = 100, PlayOrigin origin = PlayOrigin.Manual) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Le fort", parts ?? [], origin, seconds, null, "Louis XIV fait bâtir le fort.", "fr");

    [Fact]
    public async Task A_story_without_audio_is_read_by_the_device_and_counted_as_a_story_started()
    {
        List<PlayRequest> started = [];
        _controller.MainStarted += started.Add;

        await _controller.PlayNowAsync(TextOnly());

        _narrator.Calls.ShouldBe(["speak:fr:1:Louis XIV fait bâtir le fort."]);
        _player.Calls.ShouldBeEmpty();
        _controller.State.Phase.ShouldBe(PlaybackPhase.Playing);
        _controller.State.Narrated.ShouldBeTrue();
        _controller.State.Current!.Role.ShouldBe(AudioRole.Main);
        started.Count.ShouldBe(1);
        _analytics.Names.ShouldBe(["audio_started"]);
    }

    [Fact]
    public async Task Progress_moves_the_position_the_end_completes_the_story_and_the_80_percent_signal_is_given()
    {
        List<ListeningSignal> signals = [];
        List<bool> ended = [];
        _controller.Listening += (_, signal) => signals.Add(signal);
        _controller.StoryEnded += (_, completed) => ended.Add(completed);
        await _controller.PlayNowAsync(TextOnly(seconds: 100));

        _narrator.Raise(new NarrationEvent.Progress(0.5));
        _controller.State.Current!.Position.ShouldBe(TimeSpan.FromSeconds(50));
        _controller.State.Current.Duration.ShouldBe(TimeSpan.FromSeconds(100));
        _narrator.Raise(new NarrationEvent.Progress(0.85));
        signals.ShouldBe([ListeningSignal.Listened80]);
        _narrator.Raise(new NarrationEvent.Ended());

        ended.ShouldBe([true]);
        _controller.State.Phase.ShouldBe(PlaybackPhase.Idle);
        _controller.State.Narrated.ShouldBeFalse();
        _analytics.Names.ShouldContain("audio_completed");
    }

    [Fact]
    public async Task Pause_resume_speed_replay_and_stop_go_to_the_narrator_and_skipping_does_nothing()
    {
        await _controller.PlayNowAsync(TextOnly());

        await _controller.PauseAsync();
        await _controller.ResumeAsync();
        await _controller.SkipAsync(10);
        await _controller.SetSpeedAsync(1.5);
        await _controller.ReplayAsync();
        await _controller.StopAsync();

        _narrator.Calls.ShouldBe(["speak:fr:1:Louis XIV fait bâtir le fort.", "pause", "resume", "speed:1.5", "speak:fr:1.5:Louis XIV fait bâtir le fort.", "stop"]);
        _player.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Speed_is_unavailable_while_a_voice_that_cannot_change_its_rate_reads()
    {
        _narrator.SupportsSpeed = false;
        await _controller.PlayNowAsync(TextOnly());

        _controller.CanChangeSpeed.ShouldBeFalse();
        await _controller.SetSpeedAsync(1.5);

        _controller.State.Speed.ShouldBe(1d);
        _narrator.Calls.ShouldNotContain("speed:1.5");
    }

    [Fact]
    public async Task A_discovery_story_without_audio_plays_the_jingle_then_the_device_reads_the_text()
    {
        List<string> mainStarted = [];
        _controller.MainStarted += request => mainStarted.Add(request.Title);

        await _controller.PlayNowAsync(TextOnly([new AudioSource("asset://jingle", AudioRole.Jingle)], origin: PlayOrigin.Discovery));
        _player.Calls.ShouldBe(["play:Jingle"]);
        _narrator.Calls.ShouldBeEmpty();
        mainStarted.ShouldBeEmpty();

        _player.Raise(new AudioPlayerEvent.Ended());

        _narrator.Calls.ShouldBe(["speak:fr:1:Louis XIV fait bâtir le fort."]);
        mainStarted.ShouldBe(["Le fort"]);
    }

    [Fact]
    public async Task A_story_with_recorded_audio_never_uses_the_device_voice()
    {
        await _controller.PlayNowAsync(TextOnly([new AudioSource("https://media/main.mp3", AudioRole.Main)]));

        _player.Calls.ShouldBe(["play:Main"]);
        _narrator.Calls.ShouldBeEmpty();
        _controller.State.Narrated.ShouldBeFalse();
    }

    [Fact]
    public async Task Without_a_voice_or_without_text_the_story_says_so_and_nothing_plays()
    {
        _narrator.IsAvailable = false;
        await _controller.PlayNowAsync(TextOnly());
        _controller.State.Error.ShouldBe("La lecture à voix haute n'est pas disponible sur cet appareil.");
        _controller.State.Phase.ShouldBe(PlaybackPhase.Idle);

        _narrator.IsAvailable = true;
        await _controller.PlayNowAsync(TextOnly() with { NarrationText = " " });
        _controller.State.Error.ShouldBe("Cette histoire n'a pas d'audio.");
        _narrator.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_narration_failure_ends_the_story_without_completing_it()
    {
        List<bool> ended = [];
        _controller.StoryEnded += (_, completed) => ended.Add(completed);
        await _controller.PlayNowAsync(TextOnly());

        _narrator.Raise(new NarrationEvent.Failed("no voice"));

        ended.ShouldBe([false]);
        _controller.State.Error.ShouldBe("La lecture a échoué.");
    }

    [Fact]
    public async Task The_voice_notice_still_applies_to_a_device_voice()
    {
        await _controller.PlayNowAsync(TextOnly());

        _controller.State.ShowAiNotice.ShouldBeTrue();
        AudioSettings.DeviceVoiceNotice.ShouldContain("synthèse");
    }
}
