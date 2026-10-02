using System.Globalization;

namespace OnVoyage.App.Core.Audio;

/// <summary>
/// The player of F-06: one story playing and at most one waiting, speeds 1 / 1.25 / 1.5, ±10 s, a pause when something takes the audio
/// (and a resume if it lets go within 30 s), the one-time notice about the synthetic voice, and the listening events.
/// It talks to the platform only through <see cref="IAudioPlayer"/>, so all of it runs in unit tests.
/// </summary>
public sealed class AudioPlaybackController
{
    public const string AiNoticeFlag = "ai_voice_notice_seen";

    private readonly IAudioPlayer _player;
    private readonly ITextNarrator _narrator;
    private readonly IAnalyticsSink _analytics;
    private readonly IFlagStore _flags;
    private readonly TimeProvider _clock;
    private readonly AudioSettings _settings;
    private readonly object _gate = new();

    private PlayRequest? _current;
    private int _partIndex;
    private PlayRequest? _waiting;
    private TimeSpan _position;
    private TimeSpan _duration;
    private PlaybackPhase _phase = PlaybackPhase.Idle;
    private double _speed = 1d;
    private bool _noticePending;
    private string? _error;
    private double _highestPercent;
    private bool _reported80;
    private int _reportedMilestone;
    private bool _completed;
    private bool _wasPlayingBeforeInterruption;
    private DateTimeOffset? _interruptedAt;
    private readonly HashSet<Guid> _playedThisSession = [];

    public AudioPlaybackController(IAudioPlayer player, IAnalyticsSink analytics, IFlagStore flags, TimeProvider clock, AudioSettings? settings = null, ITextNarrator? narrator = null)
    {
        _player = player;
        _narrator = narrator ?? new NullTextNarrator();
        _narrator.Event += OnNarrationEvent;
        _analytics = analytics;
        _flags = flags;
        _clock = clock;
        _settings = settings ?? new AudioSettings();
        _player.Event += OnPlayerEvent;
    }

    /// <summary>Raised after every change of <see cref="State"/>.</summary>
    public event Action? Changed;

    /// <summary>The main part of a story began (after the jingle and the announcement, if any). The discovery mode moves its own state on this.</summary>
    public event Action<PlayRequest>? MainStarted;

    /// <summary>A story is over: finished, skipped or stopped. <c>Completed</c> is true when it was heard to the end.</summary>
    public event Action<PlayRequest, bool>? StoryEnded;

    /// <summary>Learning signals of §6.2: the story was heard to 80 %, heard again, or dropped within its first 20 seconds.</summary>
    public event Action<PlayRequest, ListeningSignal>? Listening;

    public PlaybackState State { get; private set; } = PlaybackState.Initial;

    public bool IsBusy => _current is not null;

    /// <summary>Plays now (a tap on ▶), replacing what is playing. Listening to the same story again counts as a replay.</summary>
    public async Task PlayNowAsync(PlayRequest request, CancellationToken cancellationToken = default)
    {
        await EndCurrentAsync(skipped: true, startNext: false);
        _waiting = null;
        await StartAsync(request, cancellationToken);
    }

    /// <summary>Starts the story if nothing plays, otherwise keeps it as the one waiting (a newer one replaces an older waiting one).</summary>
    public async Task EnqueueAsync(PlayRequest request, CancellationToken cancellationToken = default)
    {
        if (_current is null)
        {
            await StartAsync(request, cancellationToken);
            return;
        }

        _waiting = request;
        Publish();
    }

    public async Task PauseAsync()
    {
        if (_phase != PlaybackPhase.Playing)
        {
            return;
        }

        await PauseOutputAsync();
        _phase = PlaybackPhase.Paused;
        Publish();
    }

    public async Task ResumeAsync()
    {
        if (_phase != PlaybackPhase.Paused)
        {
            return;
        }

        await ResumeOutputAsync();
        _phase = PlaybackPhase.Playing;
        Publish();
    }

    public Task TogglePauseAsync() => _phase == PlaybackPhase.Playing ? PauseAsync() : ResumeAsync();

    /// <summary>Back or forward by 10 s inside the main part, never past its ends.</summary>
    public async Task SkipAsync(double seconds)
    {
        if (_current is null || _duration <= TimeSpan.Zero || IsNarrating)
        {
            return; // a voice read by the device cannot be moved by seconds
        }

        var target = _position + TimeSpan.FromSeconds(seconds);
        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }

        if (target > _duration)
        {
            target = _duration;
        }

        await _player.SeekAsync(target);
        _position = target;
        Publish();
    }

    /// <summary>True where the platform has a voice to read the text of a story that has no recording.</summary>
    public bool CanNarrate => _narrator.IsAvailable;

    /// <summary>False while the device reads a story with a voice whose rate cannot change (the speed button is then hidden).</summary>
    public bool CanChangeSpeed => !IsNarrating || _narrator.SupportsSpeed;

    public async Task SetSpeedAsync(double speed)
    {
        if (!AudioSettings.Speeds.Contains(speed) || speed == _speed || !CanChangeSpeed)
        {
            return;
        }

        _speed = speed;
        if (_current is not null)
        {
            if (IsNarrating)
            {
                await _narrator.SetSpeedAsync(speed);
            }
            else
            {
                await _player.SetSpeedAsync(speed);
            }

            Track("audio_speed_changed", ("speed", speed));
        }

        Publish();
    }

    /// <summary>Cycles 1× → 1.25× → 1.5× → 1×.</summary>
    public Task CycleSpeedAsync() => SetSpeedAsync(AudioSettings.Speeds[(AudioSettings.Speeds.IndexOf(_speed) + 1) % AudioSettings.Speeds.Length]);

    /// <summary>Starts the current story over (an "audio_replayed").</summary>
    public async Task ReplayAsync()
    {
        if (_current is null)
        {
            return;
        }

        Track("audio_replayed");
        Listening?.Invoke(_current, ListeningSignal.Replay);
        _highestPercent = 0;
        _reported80 = false;
        _reportedMilestone = 0;
        _completed = false;
        if (IsNarrating)
        {
            await _narrator.SpeakAsync(_current.NarrationText!, _current.NarrationLanguage, _speed, CancellationToken.None);
        }
        else
        {
            await _player.SeekAsync(TimeSpan.Zero);
        }

        _position = TimeSpan.Zero;
        Publish();
    }

    /// <summary>Stops what plays (the traveler's choice). The next story, if one waits, does not start by itself: stopping means stopping.</summary>
    public async Task StopAsync()
    {
        _waiting = null;
        await EndCurrentAsync(skipped: true, startNext: false);
    }

    /// <summary>"Suivante": drops what plays and starts the waiting story, if any.</summary>
    public Task NextAsync() => EndCurrentAsync(skipped: true, startNext: true);

    public async Task AcknowledgeAiNoticeAsync(CancellationToken cancellationToken = default)
    {
        _noticePending = false;
        await _flags.SetAsync(AiNoticeFlag, true, cancellationToken);
        Publish();
    }

    private async Task StartAsync(PlayRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasMainAudio)
        {
            // A story published without audio is read by the device from its text, when the platform has a voice.
            if (!request.HasNarration || !_narrator.IsAvailable)
            {
                _error = request.HasNarration ? "La lecture à voix haute n'est pas disponible sur cet appareil." : "Cette histoire n'a pas d'audio.";
                Publish();
                return;
            }

            request = request with { Parts = [.. request.Parts.Where(part => part.Role != AudioRole.Main), new AudioSource(AudioSource.NarrationUri, AudioRole.Main)] };
        }

        _error = null;
        _current = request;
        _partIndex = 0;
        _position = TimeSpan.Zero;
        _duration = TimeSpan.Zero;
        _highestPercent = 0;
        _reported80 = false;
        _reportedMilestone = 0;
        _completed = false;
        _interruptedAt = null;
        _wasPlayingBeforeInterruption = false;
        if (!_noticePending && !await _flags.GetAsync(AiNoticeFlag, cancellationToken))
        {
            _noticePending = true;
        }

        await PlayPartAsync(cancellationToken);
    }

    private async Task PlayPartAsync(CancellationToken cancellationToken = default)
    {
        var request = _current!;
        var part = request.Parts[_partIndex];
        _phase = PlaybackPhase.Loading;
        Publish();
        try
        {
            if (part.IsNarration)
            {
                await _narrator.SpeakAsync(request.NarrationText!, request.NarrationLanguage, _speed, cancellationToken);
            }
            else
            {
                await _player.PlayAsync(part, _speed, request.Title, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FailAsync();
            return;
        }

        _phase = PlaybackPhase.Playing;
        if (part.Role == AudioRole.Main)
        {
            if (!_playedThisSession.Add(request.StoryId))
            {
                Track("audio_replayed");
                Listening?.Invoke(request, ListeningSignal.Replay);
            }

            Track("audio_started", ("origin", request.Origin.ToString().ToLowerInvariant()));
            MainStarted?.Invoke(request);
        }

        Publish();
    }

    /// <summary>True while the current part is the device reading the text of the story.</summary>
    private bool IsNarrating => _current is not null && _current.Parts[_partIndex].IsNarration;

    private Task PauseOutputAsync() => IsNarrating ? _narrator.PauseAsync() : _player.PauseAsync();

    private Task ResumeOutputAsync() => IsNarrating ? _narrator.ResumeAsync() : _player.ResumeAsync();

    private async Task StopOutputAsync()
    {
        if (IsNarrating)
        {
            await _narrator.StopAsync();
        }
        else
        {
            await _player.StopAsync();
        }
    }

    /// <summary>The device voice reports a share of the text read; the duration is the one announced for the story, or an estimate from the length of the text.</summary>
    private void OnNarrationEvent(NarrationEvent narration)
    {
        if (!IsNarrating)
        {
            return;
        }

        switch (narration)
        {
            case NarrationEvent.Progress progress:
                var duration = TimeSpan.FromSeconds(_current!.DurationSeconds is > 0 ? _current.DurationSeconds.Value : EstimateSeconds(_current.NarrationText!));
                OnPosition(new AudioPlayerEvent.PositionChanged(duration * Math.Clamp(progress.Fraction, 0d, 1d), duration));
                break;
            case NarrationEvent.Ended:
                _ = OnPartEndedAsync();
                break;
            case NarrationEvent.Failed:
                _ = FailAsync();
                break;
        }
    }

    /// <summary>About 150 words a minute, at normal speed.</summary>
    private static double EstimateSeconds(string text) => Math.Max(1d, text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length / 2.5d);

    private void OnPlayerEvent(AudioPlayerEvent playerEvent)
    {
        switch (playerEvent)
        {
            case AudioPlayerEvent.PositionChanged changed:
                OnPosition(changed);
                break;
            case AudioPlayerEvent.Ended:
                _ = OnPartEndedAsync();
                break;
            case AudioPlayerEvent.Failed failed:
                _ = FailAsync();
                break;
            case AudioPlayerEvent.Interruption interruption:
                _ = OnInterruptionAsync(interruption.Began);
                break;
            case AudioPlayerEvent.RemoteCommand command:
                _ = command.Key switch
                {
                    RemoteKey.Play => ResumeAsync(),
                    RemoteKey.Pause => PauseAsync(),
                    RemoteKey.Back => SkipAsync(-AudioSettings.SkipSeconds),
                    _ => SkipAsync(AudioSettings.SkipSeconds),
                };
                break;
        }
    }

    private void OnPosition(AudioPlayerEvent.PositionChanged changed)
    {
        if (_current is null)
        {
            return;
        }

        _position = changed.Position;
        _duration = changed.Duration;
        if (_current.Parts[_partIndex].Role == AudioRole.Main && changed.Duration > TimeSpan.Zero)
        {
            var percent = changed.Position / changed.Duration * 100d;
            _highestPercent = Math.Max(_highestPercent, percent);
            if (_highestPercent >= 80d && !_reported80)
            {
                _reported80 = true;
                Listening?.Invoke(_current, ListeningSignal.Listened80);
            }

            foreach (var milestone in new[] { 25, 50, 75 })
            {
                if (_highestPercent >= milestone && _reportedMilestone < milestone)
                {
                    _reportedMilestone = milestone;
                    Track("audio_progress", ("percent", milestone));
                }
            }

            if (_highestPercent >= 95d && !_completed)
            {
                _completed = true;
                Track("audio_completed");
            }
        }

        Publish();
    }

    private async Task OnPartEndedAsync()
    {
        if (_current is null)
        {
            return;
        }

        if (_partIndex + 1 < _current.Parts.Count)
        {
            _partIndex++;
            _position = TimeSpan.Zero;
            _duration = TimeSpan.Zero;
            await PlayPartAsync();
            return;
        }

        // The file ended: whatever the last position event said, the story was heard to the end.
        if (!_completed)
        {
            _completed = true;
            Track("audio_completed");
        }

        await EndCurrentAsync(skipped: false, startNext: true);
    }

    private async Task OnInterruptionAsync(bool began)
    {
        if (_current is null)
        {
            return;
        }

        if (began)
        {
            _interruptedAt = _clock.GetUtcNow();
            _wasPlayingBeforeInterruption = _phase == PlaybackPhase.Playing;
            if (_wasPlayingBeforeInterruption)
            {
                await PauseOutputAsync();
                _phase = PlaybackPhase.Paused;
                Publish();
            }

            return;
        }

        var beganAt = _interruptedAt;
        _interruptedAt = null;
        if (_wasPlayingBeforeInterruption && beganAt is { } start && (_clock.GetUtcNow() - start).TotalSeconds < _settings.ResumeAfterInterruptionSeconds)
        {
            await ResumeAsync();
        }

        _wasPlayingBeforeInterruption = false;
    }

    private async Task FailAsync()
    {
        var request = _current;
        _current = null;
        _phase = PlaybackPhase.Idle;
        _error = "La lecture a échoué.";
        Publish();
        if (request is not null)
        {
            StoryEnded?.Invoke(request, false);
        }

        await StartWaitingAsync();
    }

    private async Task EndCurrentAsync(bool skipped, bool startNext)
    {
        var request = _current;
        if (request is null)
        {
            return;
        }

        if (skipped)
        {
            await StopOutputAsync();
            if (!_completed && _highestPercent < 10d && _position < TimeSpan.FromSeconds(20) && _current?.Parts[_partIndex].Role == AudioRole.Main)
            {
                Listening?.Invoke(request, ListeningSignal.AbandonedEarly);
            }

            if (!_completed && _highestPercent > 0)
            {
                Track("audio_skipped", ("percent", (int)Math.Round(_highestPercent, MidpointRounding.AwayFromZero)));
            }
        }

        _current = null;
        _phase = PlaybackPhase.Idle;
        _position = TimeSpan.Zero;
        _duration = TimeSpan.Zero;
        Publish();
        StoryEnded?.Invoke(request, _completed);
        if (startNext)
        {
            await StartWaitingAsync();
        }
    }

    private async Task StartWaitingAsync()
    {
        if (_waiting is { } next)
        {
            _waiting = null;
            await StartAsync(next, CancellationToken.None);
        }
    }

    private void Track(string name, params (string Key, object? Value)[] extra)
    {
        if (_current is null)
        {
            return;
        }

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["story_id"] = _current.StoryId.ToString(),
            ["poi_id"] = _current.PoiId.ToString(),
        };
        foreach (var (key, value) in extra)
        {
            properties[key] = value;
        }

        _analytics.Track(name, properties);
    }

    private void Publish()
    {
        NowPlaying? now = _current is null
            ? null
            : new NowPlaying(_current.StoryId, _current.PoiId, _current.Title, _current.Parts[_partIndex].Role, _position, _duration, _current.Origin);
        State = new PlaybackState(_phase, now, _waiting?.Title, _speed, _noticePending, _error, IsNarrating);
        Changed?.Invoke();
    }

    public static string FormatTime(TimeSpan time) => $"{(int)time.TotalMinutes:00}:{time.Seconds:00}".ToString(CultureInfo.InvariantCulture);
}
