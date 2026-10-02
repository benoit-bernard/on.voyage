using AVFoundation;
using CoreLocation;
using Foundation;
using OnVoyage.App.Core.Background;
using OnVoyage.App.Core.Discovery;

namespace OnVoyage.App;

/// <summary>
/// Position updates on iPhone (T-612, §14.7). In the background mode the manager keeps updating with the screen locked
/// (<c>UIBackgroundModes</c> <c>location</c>, authorization "Always" or "While using" with the system's blue indicator), never pauses by itself,
/// and also watches significant location changes, which lets the system relaunch the app after it was terminated. The stays ("visits") are
/// computed on the device by the trigger engine, so CLVisit monitoring is not used. Not compiled in the repository's own CI image (needs macOS):
/// see docs/MOBILE.md.
/// </summary>
internal sealed class IosLocationUpdates : IPlatformLocationUpdates
{
    private CLLocationManager? _manager;
    private LocationUpdateMode _mode;

    public event Action<LocationFix>? FixReceived;

    public Task<bool> StartAsync(LocationUpdateMode mode, CancellationToken cancellationToken) =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (_manager is not null)
            {
                return true;
            }

            if (!CLLocationManager.LocationServicesEnabled)
            {
                return false;
            }

            _mode = mode;
            _manager = new CLLocationManager
            {
                DesiredAccuracy = CLLocation.AccuracyBest,
                DistanceFilter = 5d,
                ActivityType = CLActivityType.Other,
                PausesLocationUpdatesAutomatically = false,
                Delegate = new Listener(this),
            };
            if (mode == LocationUpdateMode.Background)
            {
                _manager.AllowsBackgroundLocationUpdates = true;
                _manager.ShowsBackgroundLocationIndicator = true;
                _manager.StartMonitoringSignificantLocationChanges();
            }

            _manager.StartUpdatingLocation();
            return true;
        });

    public Task RestartAsync(CancellationToken cancellationToken) =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            _manager?.StopUpdatingLocation();
            _manager?.StartUpdatingLocation();
        });

    public Task StopAsync() =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (_manager is not { } manager)
            {
                return;
            }

            manager.StopUpdatingLocation();
            if (_mode == LocationUpdateMode.Background)
            {
                manager.StopMonitoringSignificantLocationChanges();
                manager.AllowsBackgroundLocationUpdates = false;
            }

            manager.Delegate = null!;
            manager.Dispose();
            _manager = null;
        });

    private void OnLocations(CLLocation[] locations)
    {
        foreach (var location in locations)
        {
            if (location.HorizontalAccuracy < 0)
            {
                continue; // the system could not determine the position
            }

            FixReceived?.Invoke(new LocationFix(
                location.Coordinate.Latitude,
                location.Coordinate.Longitude,
                location.HorizontalAccuracy,
                location.Speed >= 0 ? location.Speed : null,
                location.Course >= 0 ? location.Course : null,
                new DateTimeOffset((DateTime)location.Timestamp)));
        }
    }

    private sealed class Listener(IosLocationUpdates owner) : CLLocationManagerDelegate
    {
        public override void LocationsUpdated(CLLocationManager manager, CLLocation[] locations) => owner.OnLocations(locations);
    }
}

/// <summary>
/// The audio session of the discovery mode: category <c>playback</c> in the spoken-audio mode, which with <c>UIBackgroundModes</c> <c>audio</c> lets
/// a story play with the screen locked and interrupts other spoken audio (a podcast) instead of mixing with it. Interruptions (a call, Siri) are
/// reported by <see cref="MediaElementAudioPlayer"/>.
/// </summary>
internal sealed class IosBackgroundSession : IBackgroundSession
{
    public bool IsSupported => true;

    // The system indicator of the location use has no "Stop" button of ours.
    public event Action? StopRequested
    {
        add { }
        remove { }
    }

    public Task StartAsync(BackgroundSessionRequest request, CancellationToken cancellationToken)
    {
        var session = AVAudioSession.SharedInstance();
        session.SetCategory(AVAudioSessionCategory.Playback, AVAudioSessionMode.SpokenAudio, AVAudioSessionCategoryOptions.InterruptSpokenAudioAndMixWithOthers, out var categoryError);
        if (categoryError is not null)
        {
            throw new InvalidOperationException(categoryError.LocalizedDescription);
        }

        session.SetActive(true, out var activeError);
        if (activeError is not null)
        {
            throw new InvalidOperationException(activeError.LocalizedDescription);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        AVAudioSession.SharedInstance().SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);
        return Task.CompletedTask;
    }
}
