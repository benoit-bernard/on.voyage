namespace OnVoyage.App.Core.Background;

public enum HealthAction
{
    None,

    /// <summary>No position for too long: ask the system for updates again.</summary>
    RestartUpdates,

    /// <summary>Restarting did not help and the system restricts the app: tell the traveler about the battery optimization.</summary>
    AdviseBatteryOptimization,
}

/// <summary>The thresholds of the stall detection. Local values, not in the remote configuration.</summary>
public sealed record BackgroundHealthSettings
{
    /// <summary>Silence after which the updates are asked for again; doubled after each failed attempt.</summary>
    public TimeSpan FirstStall { get; init; } = TimeSpan.FromSeconds(90);

    public int MaxRestarts { get; init; } = 3;

    public TimeSpan CheckEvery { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Doze and app standby may quietly drop the position updates of an app in the background. This pure rule decides what to do when they stop
/// arriving: ask again (up to <see cref="BackgroundHealthSettings.MaxRestarts"/> times, waiting twice as long each time), then, if the system
/// still restricts the app, advise the traveler once. A tunnel looks the same as a stall; asking again does no harm, and the trigger engine
/// itself never extrapolates.
/// </summary>
public sealed class BackgroundHealthMonitor(BackgroundHealthSettings settings)
{
    private DateTimeOffset _lastSign;
    private int _restarts;
    private bool _advised;

    /// <summary>The updates were just started or restarted.</summary>
    public void Begin(DateTimeOffset now)
    {
        _lastSign = now;
        _restarts = 0;
        _advised = false;
    }

    public void OnFix(DateTimeOffset now)
    {
        _lastSign = now;
        _restarts = 0;
    }

    public HealthAction Evaluate(DateTimeOffset now, bool systemRestrictsApp)
    {
        var allowedSilence = TimeSpan.FromTicks(settings.FirstStall.Ticks << Math.Min(_restarts, 10));
        if (now - _lastSign < allowedSilence)
        {
            return HealthAction.None;
        }

        if (_restarts < settings.MaxRestarts)
        {
            _restarts++;
            return HealthAction.RestartUpdates;
        }

        if (systemRestrictsApp && !_advised)
        {
            _advised = true;
            return HealthAction.AdviseBatteryOptimization;
        }

        return HealthAction.None;
    }
}
