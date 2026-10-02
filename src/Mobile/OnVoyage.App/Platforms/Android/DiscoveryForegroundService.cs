using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;

namespace OnVoyage.App;

/// <summary>
/// The foreground service that keeps the discovery mode alive with the screen locked (T-612, §14.7): types <c>location</c> and
/// <c>mediaPlayback</c>, a persistent notification "ON.VOYAGE vous accompagne" with an "Arrêter" action. It holds no position and plays nothing
/// itself: positions come from <see cref="MauiLocationSource"/>, the sound from the media service of the MediaElement; this service only gives the
/// process the standing the system requires to keep both running. Not compiled in the repository's own CI image: see docs/MOBILE.md.
/// </summary>
[Service(Name = "voyage.on.app.DiscoveryForegroundService", Exported = false, ForegroundServiceType = ForegroundService.TypeLocation | ForegroundService.TypeMediaPlayback)]
public sealed class DiscoveryForegroundService : Service
{
    public const string ActionStart = "voyage.on.app.action.START_DISCOVERY";
    public const string ActionStop = "voyage.on.app.action.STOP_DISCOVERY";
    public const string ExtraTitle = "title";
    public const string ExtraText = "text";
    public const string ExtraStopLabel = "stop_label";

    private const string ChannelId = "discovery";
    private const int NotificationId = 4101;

    /// <summary>The traveler pressed "Arrêter" in the notification. Raised on the main thread; the app turns the discovery mode off.</summary>
    public static event Action? StopRequested;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            Shutdown();
            StopRequested?.Invoke();
            return StartCommandResult.NotSticky;
        }

        var title = intent?.GetStringExtra(ExtraTitle) ?? "ON.VOYAGE vous accompagne";
        var text = intent?.GetStringExtra(ExtraText) ?? string.Empty;
        var stopLabel = intent?.GetStringExtra(ExtraStopLabel) ?? "Arrêter";
        EnsureChannel();
        var notification = BuildNotification(title, text, stopLabel);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeLocation | ForegroundService.TypeMediaPlayback);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }

        // Not sticky: after a kill the service is not recreated without the app, since the discovery mode must be turned on by the traveler.
        return StartCommandResult.NotSticky;
    }

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        // The traveler swiped the app away: the discovery mode ends with it (no hidden tracking).
        Shutdown();
        StopRequested?.Invoke();
        base.OnTaskRemoved(rootIntent);
    }

    private void Shutdown()
    {
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    private void EnsureChannel()
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager is null || manager.GetNotificationChannel(ChannelId) is not null)
        {
            return;
        }

        // Low importance: no sound, no pop-up. The notification exists to be seen in the shade and the status bar.
        var channel = new NotificationChannel(ChannelId, "Mode découverte", NotificationImportance.Low)
        {
            Description = "Reste affichée tant que ON.VOYAGE vous raconte les lieux autour de vous.",
        };
        channel.SetShowBadge(false);
        manager.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification(string title, string text, string stopLabel)
    {
        var stop = new Intent(this, typeof(DiscoveryForegroundService)).SetAction(ActionStop);
        var stopIntent = PendingIntent.GetService(this, 0, stop, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var launch = PackageManager?.GetLaunchIntentForPackage(PackageName ?? string.Empty);
        var openIntent = launch is null ? null : PendingIntent.GetActivity(this, 0, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var stopIcon = Icon.CreateWithResource(this, Android.Resource.Drawable.IcMenuCloseClearCancel);

        var builder = new Notification.Builder(this, ChannelId)
            .SetContentTitle(title)
            .SetContentText(text)
            .SetSmallIcon(Android.Resource.Drawable.IcMenuMyLocation)
            .SetOngoing(true)
            .SetCategory(Notification.CategoryService)
            .SetVisibility(NotificationVisibility.Public)
            .AddAction(new Notification.Action.Builder(stopIcon, stopLabel, stopIntent).Build());
        if (openIntent is not null)
        {
            builder.SetContentIntent(openIntent);
        }

        return builder.Build();
    }
}
