using Android.Content;
using Android.Media;
using Android.OS;
using OnVoyage.App.Core.Background;

namespace OnVoyage.App;

/// <summary>
/// <see cref="IBackgroundSession"/> on Android: starts and stops <see cref="DiscoveryForegroundService"/>. The notification permission of Android 13
/// is asked here, when the background mode starts; a refusal is not an error (the service still runs, its notification is only hidden).
/// Not compiled in the repository's own CI image: see docs/MOBILE.md.
/// </summary>
internal sealed class AndroidBackgroundSession : IBackgroundSession
{
    public AndroidBackgroundSession() => DiscoveryForegroundService.StopRequested += () => StopRequested?.Invoke();

    public event Action? StopRequested;

    public bool IsSupported => true;

    public async Task StartAsync(BackgroundSessionRequest request, CancellationToken cancellationToken)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
                {
                    await Permissions.RequestAsync<Permissions.PostNotifications>();
                }
            });
        }

        var context = Android.App.Application.Context;
        var intent = new Intent(context, typeof(DiscoveryForegroundService))
            .SetAction(DiscoveryForegroundService.ActionStart)
            .PutExtra(DiscoveryForegroundService.ExtraTitle, request.Title)
            .PutExtra(DiscoveryForegroundService.ExtraText, request.Text)
            .PutExtra(DiscoveryForegroundService.ExtraStopLabel, request.StopLabel);
        try
        {
            context.StartForegroundService(intent);
        }
        catch (Java.Lang.IllegalStateException ex)
        {
            // ForegroundServiceStartNotAllowedException (Android 12+): the system refused to start it from here.
            throw new InvalidOperationException("The system refused the foreground service.", ex);
        }
        catch (Java.Lang.SecurityException ex)
        {
            throw new UnauthorizedAccessException("The foreground service types are not allowed.", ex);
        }
    }

    public Task StopAsync()
    {
        var context = Android.App.Application.Context;
        context.StopService(new Intent(context, typeof(DiscoveryForegroundService)));
        return Task.CompletedTask;
    }
}

/// <summary>Doze and app standby: whether the app may still be restricted in the background, and the system screen where the traveler can exempt it.</summary>
internal sealed class AndroidBatteryOptimization : IBatteryOptimization
{
    public bool IsRestricted
    {
        get
        {
            var context = Android.App.Application.Context;
            var power = (PowerManager?)context.GetSystemService(Context.PowerService);
            return power is not null && !power.IsIgnoringBatteryOptimizations(context.PackageName);
        }
    }

    public Task OpenSettingsAsync()
    {
        // The list of apps where the optimization can be turned off: no special permission (the direct "ignore" request is restricted by Google Play).
        var intent = new Intent(Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings).AddFlags(ActivityFlags.NewTask);
        Android.App.Application.Context.StartActivity(intent);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Audio focus for a story read by the device voice (<c>TextToSpeech</c> does not take it by itself): other audio pauses while the story is read
/// and resumes afterwards. Recorded stories go through ExoPlayer, which manages focus for the MediaElement (to verify on a device).
/// </summary>
internal sealed class AndroidAudioFocus : IDisposable
{
    private readonly AudioManager? _manager;
    private readonly AudioFocusRequestClass? _request;

    private AndroidAudioFocus(AudioManager? manager, AudioFocusRequestClass? request)
    {
        _manager = manager;
        _request = request;
    }

    public static AndroidAudioFocus Acquire()
    {
        var manager = (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService);
        if (manager is null)
        {
            return new AndroidAudioFocus(null, null);
        }

        var attributes = new AudioAttributes.Builder()
            .SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Speech)!
            .Build()!;
        var request = new AudioFocusRequestClass.Builder(AudioFocus.GainTransient)
            .SetAudioAttributes(attributes)!
            .Build()!;
        manager.RequestAudioFocus(request);
        return new AndroidAudioFocus(manager, request);
    }

    public void Dispose()
    {
        if (_manager is not null && _request is not null)
        {
            _manager.AbandonAudioFocusRequest(_request);
        }
    }
}
