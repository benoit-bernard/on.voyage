using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Discovery;

namespace OnVoyage.App.Core.Background;

/// <summary>
/// The <see cref="ILocationSource"/> of the phone apps from the MVP on (T-612): it runs the permission path, starts the platform background
/// session (foreground service and notification, audio session), then the platform position updates in the mode the permission allows, and
/// watches that the updates keep coming. With "Always" refused, or on a platform without background session, it falls back to the foreground
/// mode instead of failing. Positions are relayed and dropped; nothing is stored here.
/// </summary>
public sealed class BackgroundLocationSource : ILocationSource, IBackgroundStatus, IDisposable
{
    private readonly IPlatformLocationUpdates _platform;
    private readonly BackgroundAccessFlow _access;
    private readonly IBackgroundSession _session;
    private readonly IBatteryOptimization _battery;
    private readonly IAnalyticsSink _analytics;
    private readonly TimeProvider _clock;
    private readonly BackgroundHealthSettings _health;
    private readonly BackgroundHealthMonitor _monitor;
    private readonly object _lock = new();
    private ITimer? _timer;
    private bool _running;
    private int _checking;

    public BackgroundLocationSource(
        IPlatformLocationUpdates platform,
        BackgroundAccessFlow access,
        IBackgroundSession session,
        IBatteryOptimization battery,
        IAnalyticsSink analytics,
        TimeProvider clock,
        BackgroundHealthSettings? health = null)
    {
        _platform = platform;
        _access = access;
        _session = session;
        _battery = battery;
        _analytics = analytics;
        _clock = clock;
        _health = health ?? new BackgroundHealthSettings();
        _monitor = new BackgroundHealthMonitor(_health);
        platform.FixReceived += OnPlatformFix;
        session.StopRequested += OnSessionStopRequested;
    }

    public event Action<LocationFix>? FixReceived;

    public event Action? Changed;

    public event Action? StopRequested;

    public LocationAccess Access { get; private set; } = LocationAccess.Denied;

    public BackgroundAdvice Advice { get; private set; }

    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        if (_running)
        {
            return true;
        }

        var access = await _access.RequestAsync(_session.IsSupported, cancellationToken);
        if (access == LocationAccess.Denied)
        {
            Access = access;
            Changed?.Invoke();
            return false;
        }

        if (access == LocationAccess.Background && !await TryStartSessionAsync(cancellationToken))
        {
            access = LocationAccess.Foreground;
        }

        var started = await _platform.StartAsync(access == LocationAccess.Background ? LocationUpdateMode.Background : LocationUpdateMode.Foreground, cancellationToken);
        if (!started)
        {
            if (access == LocationAccess.Background)
            {
                await _session.StopAsync();
            }

            Access = LocationAccess.Denied;
            Changed?.Invoke();
            return false;
        }

        Access = access;
        Advice = BackgroundAdvice.None;
        _running = true;
        if (access == LocationAccess.Background)
        {
            _monitor.Begin(_clock.GetUtcNow());
            _timer = _clock.CreateTimer(_ => _ = CheckAsync(), null, _health.CheckEvery, _health.CheckEvery);
        }

        Changed?.Invoke();
        return true;
    }

    public async Task StopAsync()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _timer?.Dispose();
        _timer = null;
        await _platform.StopAsync();
        if (Access == LocationAccess.Background)
        {
            await _session.StopAsync();
        }

        Access = LocationAccess.Denied;
        Changed?.Invoke();
    }

    public Task OpenBatterySettingsAsync() => _battery.OpenSettingsAsync();

    public void Dispose()
    {
        _timer?.Dispose();
        _platform.FixReceived -= OnPlatformFix;
        _session.StopRequested -= OnSessionStopRequested;
    }

    private async Task<bool> TryStartSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _session.StartAsync(BackgroundSessionRequest.Default, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The system refused the service (restricted start, missing permission): the foreground mode still works.
            return false;
        }
    }

    private void OnPlatformFix(LocationFix fix)
    {
        lock (_lock)
        {
            _monitor.OnFix(_clock.GetUtcNow());
        }

        FixReceived?.Invoke(fix);
    }

    private void OnSessionStopRequested() => StopRequested?.Invoke();

    private async Task CheckAsync()
    {
        if (!_running || Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }

        try
        {
            HealthAction action;
            lock (_lock)
            {
                action = _monitor.Evaluate(_clock.GetUtcNow(), _battery.IsRestricted);
            }

            if (action == HealthAction.RestartUpdates)
            {
                _analytics.Track("gps_loss", new Dictionary<string, object?>());
                await _platform.RestartAsync(CancellationToken.None);
            }
            else if (action == HealthAction.AdviseBatteryOptimization)
            {
                Advice = BackgroundAdvice.BatteryOptimization;
                Changed?.Invoke();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }
}
