using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Discovery;

namespace OnVoyage.App.Core.Driving;

/// <summary>
/// Whether the car screen is on. A singleton on its own so that services that must stay quiet while driving (the feedback banner and its
/// notification) can read it without depending on the discovery controller.
/// </summary>
public sealed class CarModeState
{
    public bool IsActive { get; private set; }

    public event Action? Changed;

    internal void Set(bool active)
    {
        if (IsActive == active)
        {
            return;
        }

        IsActive = active;
        Changed?.Invoke();
    }
}

/// <summary>What the car screen shows: the story being told, the next one and how far it is (F-10).</summary>
public sealed record CarModeView(bool Active, bool Suggested, bool CanLeave, bool Playing, string? CurrentTitle, string? NextTitle, int? NextDistanceMeters, bool SignalLost)
{
    /// <summary>"450 m" under a kilometre, "1,2 km" above (rounded to 100 m).</summary>
    public static string FormatDistance(int meters) => DistanceText.Format(meters);
}

public enum CarModeStartResult
{
    Started,
    PermissionDenied,
    NothingToTell,
}

/// <summary>
/// Car mode (F-10). It is proposed when the discovery mode has been in the car travel mode for the hysteresis delay of the engine (speed above
/// 30 km/h for 30 s) and can be turned on by hand. While it is on nothing asks the traveler for a reaction (the stories wait for the trip recap)
/// and the controls are the few of the screen: play or pause, next, stop. The trigger rules (anticipation, ±60° cone, place selection) are the
/// engine's; this class only decides what is proposed and what is shown.
/// </summary>
public sealed class CarModeController : IDisposable
{
    private readonly DiscoveryModeController _discovery;
    private readonly AudioPlaybackController _audio;
    private readonly CarModeState _state;
    private bool _suggestionDeclined;

    public CarModeController(DiscoveryModeController discovery, AudioPlaybackController audio, CarModeState state)
    {
        _discovery = discovery;
        _audio = audio;
        _state = state;
        discovery.Changed += OnChanged;
        audio.Changed += OnChanged;
    }

    public event Action? Changed;

    public bool IsActive => _state.IsActive;

    /// <summary>True when the car screen is worth proposing: discovery is on, the traveler drives, and did not decline during this drive.</summary>
    public bool Suggested => !IsActive && _discovery.State is { IsOn: true, Mode: TravelMode.Car } && !_suggestionDeclined;

    public CarModeView View
    {
        get
        {
            var discovery = _discovery.State;
            var playback = _audio.State;
            return new CarModeView(
                IsActive,
                Suggested,
                CanLeave: IsActive && discovery.Mode != TravelMode.Car,
                Playing: playback.Phase == PlaybackPhase.Playing,
                CurrentTitle: playback.Current?.Title,
                NextTitle: discovery.Next?.Name,
                NextDistanceMeters: discovery.Next?.DistanceMeters,
                SignalLost: discovery.SignalLost);
        }
    }

    public void Dispose()
    {
        _discovery.Changed -= OnChanged;
        _audio.Changed -= OnChanged;
    }

    /// <summary>Turns the car screen on, starting the discovery mode first when it was off (the permission question is the discovery mode's).</summary>
    public async Task<CarModeStartResult> ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (!_discovery.State.IsOn)
        {
            var result = await _discovery.StartAsync(keepScreenOn: true, cancellationToken);
            if (result is DiscoveryStartResult.PermissionDenied)
            {
                return CarModeStartResult.PermissionDenied;
            }

            if (result is DiscoveryStartResult.NothingToTell)
            {
                return CarModeStartResult.NothingToTell;
            }
        }

        _state.Set(true);
        return CarModeStartResult.Started;
    }

    /// <summary>The traveler accepted the proposal: discovery is already on.</summary>
    public void Accept() => _state.Set(_discovery.State.IsOn);

    /// <summary>"Non merci": not proposed again until the traveler has stopped driving.</summary>
    public void DeclineSuggestion()
    {
        _suggestionDeclined = true;
        Changed?.Invoke();
    }

    /// <summary>Back to the normal screens; the discovery mode keeps going.</summary>
    public void Leave() => _state.Set(false);

    public Task TogglePauseAsync() => _audio.TogglePauseAsync();

    /// <summary>"Suivante": the waiting story starts, or the current one ends and the engine looks for the next place.</summary>
    public Task NextAsync() => _audio.NextAsync();

    /// <summary>"Arrêter": the story, the discovery mode and the car screen stop.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _audio.StopAsync();
        await _discovery.StopAsync(cancellationToken);
        _state.Set(false);
    }

    private void OnChanged()
    {
        var discovery = _discovery.State;
        if (!discovery.IsOn)
        {
            _suggestionDeclined = false;
            _state.Set(false);
        }
        else if (discovery.Mode != TravelMode.Car)
        {
            _suggestionDeclined = false; // a new drive may be proposed again
        }

        Changed?.Invoke();
    }
}
