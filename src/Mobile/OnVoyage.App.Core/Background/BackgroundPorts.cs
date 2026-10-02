using OnVoyage.App.Core.Discovery;

namespace OnVoyage.App.Core.Background;

/// <summary>What the system lets the app do with the position. "Always" is asked separately and only for the discovery mode in the background (§14.7).</summary>
public enum LocationPermissionLevel
{
    Denied,
    WhenInUse,
    Always,
}

/// <summary>What the discovery mode ends up with: no position, the position while the app is on screen, or also with the screen locked.</summary>
public enum LocationAccess
{
    Denied,
    Foreground,
    Background,
}

public enum LocationUpdateMode
{
    Foreground,
    Background,
}

/// <summary>Something the traveler can do to get the background mode working again. Shown once per session.</summary>
public enum BackgroundAdvice
{
    None,

    /// <summary>The system keeps stopping the app although it holds a foreground service: its battery optimization is the likely cause.</summary>
    BatteryOptimization,
}

/// <summary>The system permission dialogs. Each method asks the system and reports what it granted; none is called before the traveler asked for the discovery mode.</summary>
public interface ILocationPermissions
{
    Task<LocationPermissionLevel> CheckAsync(CancellationToken cancellationToken);

    Task<LocationPermissionLevel> RequestWhenInUseAsync(CancellationToken cancellationToken);

    /// <summary>"Toujours autoriser" (Android, after the foreground permission) / "Toujours" (iOS).</summary>
    Task<LocationPermissionLevel> RequestAlwaysAsync(CancellationToken cancellationToken);
}

/// <summary>The screen that explains why "Always" is needed, shown before the system dialog. True when the traveler agrees to continue.</summary>
public interface IBackgroundRationale
{
    Task<bool> ConfirmAsync(CancellationToken cancellationToken);
}

/// <summary>The text of the persistent notification (Android) and of the system indicator; "Arrêter" ends the discovery mode from outside the app.</summary>
public sealed record BackgroundSessionRequest(string Title, string Text, string StopLabel)
{
    public static BackgroundSessionRequest Default { get; } = new("ON.VOYAGE vous accompagne", "Les histoires des lieux que vous croisez, écran verrouillé.", "Arrêter");
}

/// <summary>
/// What keeps the app alive with the screen locked: the foreground service of types location and media playback with its notification
/// (Android), the audio session and the background modes (iOS). Where the platform has none, <see cref="IsSupported"/> is false.
/// </summary>
public interface IBackgroundSession
{
    bool IsSupported { get; }

    /// <summary>The traveler pressed "Arrêter" in the notification.</summary>
    event Action? StopRequested;

    Task StartAsync(BackgroundSessionRequest request, CancellationToken cancellationToken);

    Task StopAsync();
}

/// <summary>The platform position updates, in the foreground or kept alive in the background. The fixes are handed over and dropped.</summary>
public interface IPlatformLocationUpdates
{
    event Action<LocationFix>? FixReceived;

    Task<bool> StartAsync(LocationUpdateMode mode, CancellationToken cancellationToken);

    /// <summary>Asks the system for updates again after they dried up (the system may have dropped the request).</summary>
    Task RestartAsync(CancellationToken cancellationToken);

    Task StopAsync();
}

/// <summary>Android's battery optimization (Doze, app standby). Where there is none, <see cref="IsRestricted"/> is false.</summary>
public interface IBatteryOptimization
{
    /// <summary>True when the system may still restrict the app in the background.</summary>
    bool IsRestricted { get; }

    /// <summary>Opens the system screen where the traveler can exempt the app.</summary>
    Task OpenSettingsAsync();
}

public sealed class NoBatteryOptimization : IBatteryOptimization
{
    public bool IsRestricted => false;

    public Task OpenSettingsAsync() => Task.CompletedTask;
}

/// <summary>What the discovery screen shows about the background mode.</summary>
public interface IBackgroundStatus
{
    LocationAccess Access { get; }

    BackgroundAdvice Advice { get; }

    event Action? Changed;

    /// <summary>The traveler pressed "Arrêter" in the notification.</summary>
    event Action? StopRequested;

    Task OpenBatterySettingsAsync();
}
