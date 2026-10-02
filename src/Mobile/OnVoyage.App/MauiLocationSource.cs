using OnVoyage.App.Core.Background;
using OnVoyage.App.Core.Discovery;

namespace OnVoyage.App;

/// <summary>
/// Position updates through MAUI Essentials. The permission path (system dialogs, the explanation before "Always") belongs to
/// <see cref="BackgroundAccessFlow"/>; this class only listens. On Android the updates keep arriving with the screen locked as long as the process
/// is kept alive by <see cref="DiscoveryForegroundService"/> (to verify on a device: if Essentials stopped delivering in the background, the
/// fallback is a native <c>LocationManager</c> listener in the service). On iPhone the background mode has its own implementation
/// (<c>IosLocationUpdates</c>) and this one is not registered.
/// </summary>
internal sealed class MauiLocationSource : IPlatformLocationUpdates
{
    private static readonly GeolocationListeningRequest Request = new(GeolocationAccuracy.Best, TimeSpan.FromSeconds(2));

    private bool _listening;

    public event Action<LocationFix>? FixReceived;

    public async Task<bool> StartAsync(LocationUpdateMode mode, CancellationToken cancellationToken)
    {
        if (_listening)
        {
            return true;
        }

        if (await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>() != PermissionStatus.Granted)
        {
            return false;
        }

        Geolocation.Default.LocationChanged += OnLocationChanged;
        try
        {
            _listening = await Geolocation.Default.StartListeningForegroundAsync(Request);
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or FeatureNotEnabledException or PermissionException)
        {
            _listening = false;
        }

        if (!_listening)
        {
            Geolocation.Default.LocationChanged -= OnLocationChanged;
        }

        return _listening;
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        if (!_listening)
        {
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(() => Geolocation.Default.StopListeningForeground());
        _listening = false;
        Geolocation.Default.LocationChanged -= OnLocationChanged;
        await StartAsync(LocationUpdateMode.Background, cancellationToken);
    }

    public Task StopAsync()
    {
        if (_listening)
        {
            Geolocation.Default.StopListeningForeground();
            Geolocation.Default.LocationChanged -= OnLocationChanged;
            _listening = false;
        }

        return Task.CompletedTask;
    }

    private void OnLocationChanged(object? sender, GeolocationLocationChangedEventArgs args)
    {
        var location = args.Location;
        FixReceived?.Invoke(new LocationFix(location.Latitude, location.Longitude, location.Accuracy ?? 25d, location.Speed, location.Course, location.Timestamp));
    }
}

/// <summary>Keeps the screen on during discovery mode when the traveler chose to (needed in the foreground-only mode).</summary>
internal sealed class MauiKeepAwake : IScreenKeepAwake
{
    public void SetAwake(bool awake) => MainThread.BeginInvokeOnMainThread(() => DeviceDisplay.Current.KeepScreenOn = awake);
}

/// <summary>"The voice notice was seen" and the like, in the platform preferences.</summary>
internal sealed class PreferencesFlagStore : OnVoyage.App.Core.Audio.IFlagStore
{
    public Task<bool> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Preferences.Default.Get($"flag.{key}", false));

    public Task SetAsync(string key, bool value, CancellationToken cancellationToken)
    {
        Preferences.Default.Set($"flag.{key}", value);
        return Task.CompletedTask;
    }
}
