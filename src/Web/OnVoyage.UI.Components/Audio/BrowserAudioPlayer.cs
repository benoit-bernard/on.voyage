using Microsoft.JSInterop;
using OnVoyage.App.Core.Audio;

namespace OnVoyage.UI.Components.Audio;

/// <summary>
/// <see cref="IAudioPlayer"/> for the PWA: an HTML <c>&lt;audio&gt;</c> element behind a small JS module. The phone apps use the platform
/// player instead (lock screen, audio focus), so this one has no interruption events.
/// </summary>
public sealed class BrowserAudioPlayer(IJSRuntime js) : IAudioPlayer, IAsyncDisposable
{
    public const string JingleUrl = "_content/OnVoyage.UI.Components/audio/jingle.mp3";

    private IJSObjectReference? _module;
    private DotNetObjectReference<BrowserAudioPlayer>? _reference;

    public event Action<AudioPlayerEvent>? Event;

    public async Task PlayAsync(AudioSource source, double speed, string title, CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        var url = source.Uri == "asset://jingle" ? JingleUrl : source.Uri;
        await _module!.InvokeVoidAsync("play", cancellationToken, url, speed, title);
    }

    public async Task PauseAsync() => await _module!.InvokeVoidAsync("pause");

    public async Task ResumeAsync() => await _module!.InvokeVoidAsync("resume");

    public async Task SeekAsync(TimeSpan position) => await _module!.InvokeVoidAsync("seek", position.TotalSeconds);

    public async Task SetSpeedAsync(double speed) => await _module!.InvokeVoidAsync("setSpeed", speed);

    public async Task StopAsync()
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("stop");
        }
    }

    [JSInvokable]
    public void OnPosition(double seconds, double duration) => Event?.Invoke(new AudioPlayerEvent.PositionChanged(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(duration)));

    [JSInvokable]
    public void OnEnded() => Event?.Invoke(new AudioPlayerEvent.Ended());

    [JSInvokable]
    public void OnFailed(string message) => Event?.Invoke(new AudioPlayerEvent.Failed(message));

    /// <summary>The headset or lock-screen button the browser forwarded.</summary>
    [JSInvokable]
    public void OnMediaKey(string key) => Event?.Invoke(new AudioPlayerEvent.RemoteCommand(key switch
    {
        "pause" => RemoteKey.Pause,
        "back" => RemoteKey.Back,
        "forward" => RemoteKey.Forward,
        _ => RemoteKey.Play,
    }));

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            await _module.DisposeAsync();
        }

        _reference?.Dispose();
    }

    private async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (_module is not null)
        {
            return;
        }

        _module = await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, "./_content/OnVoyage.UI.Components/js/audio.js");
        _reference = DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("init", cancellationToken, _reference);
    }
}
