using OnVoyage.App.Core.Discovery;

namespace OnVoyage.App;

/// <summary>
/// Foreground positions through MAUI Essentials (discovery mode, MVP-0). The permission is requested here, when the traveler presses the
/// button, never at launch. Background and screen-locked positions arrive at the MVP with a foreground service (T-612).
/// </summary>
internal sealed class MauiLocationSource : ILocationSource
{
    private bool _listening;

    public event Action<LocationFix>? FixReceived;

    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        if (_listening)
        {
            return true;
        }

        var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        }

        if (status != PermissionStatus.Granted)
        {
            return false;
        }

        Geolocation.Default.LocationChanged += OnLocationChanged;
        try
        {
            _listening = await Geolocation.Default.StartListeningForegroundAsync(new GeolocationListeningRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(2)));
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

/// <summary>Keeps the screen on during discovery mode when the traveler chose to (the MVP-0 mode only works in the foreground).</summary>
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
