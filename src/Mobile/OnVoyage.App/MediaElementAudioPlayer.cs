using CommunityToolkit.Maui.Core.Primitives;
using CommunityToolkit.Maui.Views;
using OnVoyage.App.Core.Audio;

namespace OnVoyage.App;

/// <summary>
/// <see cref="IAudioPlayer"/> over the .NET MAUI Community Toolkit <c>MediaElement</c> (F-06): background playback and lock-screen controls
/// come from the platform service the toolkit starts (ExoPlayer on Android, AVPlayer on iOS). The element lives in <c>MainPage</c>;
/// <see cref="Attach"/> hands it over once the page is up. Not compiled in the repository's own CI image: see docs/MOBILE.md.
/// </summary>
internal sealed class MediaElementAudioPlayer : IAudioPlayer
{
    private readonly TaskCompletionSource<MediaElement> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MediaElement? _element;

    public MediaElementAudioPlayer()
    {
#if IOS
        // A call or Siri takes the audio session: tell the controller so it can resume after a short one (F-06).
        AVFoundation.AVAudioSession.Notifications.ObserveInterruption((_, args) =>
            Event?.Invoke(new AudioPlayerEvent.Interruption(args.InterruptionType == AVFoundation.AVAudioSessionInterruptionType.Began)));
#endif
    }

    public event Action<AudioPlayerEvent>? Event;

    public void Attach(MediaElement element)
    {
        if (_element is not null)
        {
            return;
        }

        _element = element;
        element.ShouldAutoPlay = false;
        element.ShouldShowPlaybackControls = false;
        element.PositionChanged += (_, args) => Event?.Invoke(new AudioPlayerEvent.PositionChanged(args.Position, element.Duration));
        element.MediaEnded += (_, _) => Event?.Invoke(new AudioPlayerEvent.Ended());
        element.MediaFailed += (_, args) => Event?.Invoke(new AudioPlayerEvent.Failed(args.ErrorMessage));
        _ready.TrySetResult(element);
    }

    public async Task PlayAsync(AudioSource source, double speed, string title, CancellationToken cancellationToken)
    {
        var element = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            element.Stop();
            element.MetadataTitle = title;
            element.MetadataArtist = "ON.VOYAGE";
            element.Source = source.Uri == "asset://jingle" ? MediaSource.FromResource("jingle.mp3") : MediaSource.FromUri(source.Uri);
            element.Speed = speed;
            element.Play();
        });
    }

    public Task PauseAsync() => OnMainAsync(element => element.Pause());

    public Task ResumeAsync() => OnMainAsync(element => element.Play());

    public async Task SeekAsync(TimeSpan position)
    {
        if (_element is { } element)
        {
            await MainThread.InvokeOnMainThreadAsync(() => element.SeekTo(position));
        }
    }

    public Task SetSpeedAsync(double speed) => OnMainAsync(element => element.Speed = speed);

    public Task StopAsync() => OnMainAsync(element => element.Stop());

    private Task OnMainAsync(Action<MediaElement> action) =>
        _element is { } element ? MainThread.InvokeOnMainThreadAsync(() => action(element)) : Task.CompletedTask;
}
