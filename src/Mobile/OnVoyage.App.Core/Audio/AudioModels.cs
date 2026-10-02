namespace OnVoyage.App.Core.Audio;

public enum AudioRole
{
    /// <summary>The 2-second embedded jingle that opens an automatic announcement.</summary>
    Jingle,

    /// <summary>"Devant vous…", "Sur votre gauche…", "Sur votre droite…" (generated with the story, §8.7).</summary>
    Announcement,

    /// <summary>The story itself: progress events and the listening percentage are about this part.</summary>
    Main,
}

public sealed record AudioSource(string Uri, AudioRole Role)
{
    /// <summary>Stands for "the device reads the text of the story" in the list of parts, in place of an audio file.</summary>
    public const string NarrationUri = "narration://story";

    public bool IsNarration => Uri == NarrationUri;
}

public enum PlayOrigin
{
    /// <summary>The traveler tapped ▶.</summary>
    Manual,

    /// <summary>The discovery mode decided (jingle, direction, then the story).</summary>
    Discovery,

    /// <summary>A clip of the onboarding (F-02): no feedback banner, no learning signal other than the 👍/👎 of the screen.</summary>
    Onboarding,
}

/// <summary>
/// A story to play. <see cref="Weights"/> is the place's taste vector, carried so that the feedback after the story can move the local profile
/// without another call; it is null when the caller does not have it. A story published without audio carries its <see cref="NarrationText"/>
/// instead of a main part: the device reads it aloud (<see cref="ITextNarrator"/>) in <see cref="NarrationLanguage"/>.
/// </summary>
public sealed record PlayRequest(
    Guid StoryId,
    Guid PoiId,
    string Title,
    IReadOnlyList<AudioSource> Parts,
    PlayOrigin Origin,
    int? DurationSeconds = null,
    IReadOnlyDictionary<string, double>? Weights = null,
    string? NarrationText = null,
    string NarrationLanguage = "fr")
{
    public bool HasMainAudio => Parts.Any(part => part.Role == AudioRole.Main && !part.IsNarration);

    public bool HasNarration => !string.IsNullOrWhiteSpace(NarrationText);
}

public enum PlaybackPhase
{
    Idle,
    Loading,
    Playing,
    Paused,
}

public sealed record NowPlaying(Guid StoryId, Guid PoiId, string Title, AudioRole Role, TimeSpan Position, TimeSpan Duration, PlayOrigin Origin);

/// <summary>Everything the player bar shows. The controller replaces it on each change and raises <see cref="AudioPlaybackController.Changed"/>.</summary>
public sealed record PlaybackState(
    PlaybackPhase Phase,
    NowPlaying? Current,
    string? WaitingTitle,
    double Speed,
    bool ShowAiNotice,
    string? Error,
    bool Narrated = false)
{
    public static PlaybackState Initial { get; } = new(PlaybackPhase.Idle, null, null, 1d, false, null);

    public bool IsActive => Phase != PlaybackPhase.Idle;
}

public enum RemoteKey
{
    Play,
    Pause,
    Back,
    Forward,
}

/// <summary>What the platform player reports. The controller turns these into state, queue moves and analytics.</summary>
public abstract record AudioPlayerEvent
{
    public sealed record PositionChanged(TimeSpan Position, TimeSpan Duration) : AudioPlayerEvent;

    public sealed record Ended : AudioPlayerEvent;

    public sealed record Failed(string Message) : AudioPlayerEvent;

    /// <summary>A lock-screen or headset button the platform could not handle itself.</summary>
    public sealed record RemoteCommand(RemoteKey Key) : AudioPlayerEvent;

    /// <summary>A phone call, an assistant, a navigation prompt took the audio; <paramref name="Began"/> false when it let go.</summary>
    public sealed record Interruption(bool Began) : AudioPlayerEvent;
}

/// <summary>The platform audio player (MAUI <c>MediaElement</c> in the app, a fake in tests). It plays one file at a time; the queue is the controller's.</summary>
public interface IAudioPlayer
{
    event Action<AudioPlayerEvent>? Event;

    Task PlayAsync(AudioSource source, double speed, string title, CancellationToken cancellationToken);

    Task PauseAsync();

    Task ResumeAsync();

    Task SeekAsync(TimeSpan position);

    Task SetSpeedAsync(double speed);

    Task StopAsync();
}

/// <summary>Where events about listening go (the queue of T-619 later). Properties never hold coordinates.</summary>
public interface IAnalyticsSink
{
    void Track(string name, IReadOnlyDictionary<string, object?> properties);
}

public sealed class NullAnalyticsSink : IAnalyticsSink
{
    public void Track(string name, IReadOnlyDictionary<string, object?> properties)
    {
    }
}

/// <summary>Small yes/no memory of the app ("the voice notice was shown"). Backed by user.db once it exists.</summary>
public interface IFlagStore
{
    Task<bool> GetAsync(string key, CancellationToken cancellationToken);

    Task SetAsync(string key, bool value, CancellationToken cancellationToken);
}

public sealed class InMemoryFlagStore : IFlagStore
{
    private readonly Dictionary<string, bool> _flags = [];

    public Task<bool> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(_flags.GetValueOrDefault(key));

    public Task SetAsync(string key, bool value, CancellationToken cancellationToken)
    {
        _flags[key] = value;
        return Task.CompletedTask;
    }
}

/// <summary>Playback settings of annexe E (<c>audio</c>).</summary>
public sealed record AudioSettings(double ResumeAfterInterruptionSeconds = 30d)
{
    public static System.Collections.Immutable.ImmutableArray<double> Speeds { get; } = [1d, 1.25d, 1.5d];

    public const double SkipSeconds = 10d;

    public const string VoiceNotice = "Voix générée par intelligence artificielle";

    /// <summary>Shown while the device reads a story that has no recorded voice: the voice is synthetic too, and it is the device's.</summary>
    public const string DeviceVoiceNotice = "Voix de synthèse de votre appareil";
}

/// <summary>What listening tells about taste, beyond the explicit feedback of F-07.</summary>
public enum ListeningSignal
{
    Listened80,
    Replay,
    AbandonedEarly,
}
