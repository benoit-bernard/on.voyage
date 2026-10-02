using OnVoyage.App.Core.Audio;

namespace OnVoyage.App.Core.Background;

/// <summary>
/// The permission path of the discovery mode (§14.7). The system dialog for the position is shown when the traveler turns the discovery mode on,
/// never at launch. "Always" is asked only when the platform can keep the app alive in the background, only after an explanation, and a refusal
/// is not an error: the mode works with the screen on. The result of each system dialog is reported as <c>location_permission_result</c>
/// (the level only: <c>denied</c>, <c>when_in_use</c>, <c>always</c>).
/// </summary>
public sealed class BackgroundAccessFlow(ILocationPermissions permissions, IBackgroundRationale rationale, IAnalyticsSink analytics)
{
    public async Task<LocationAccess> RequestAsync(bool wantsBackground, CancellationToken cancellationToken)
    {
        var level = await permissions.CheckAsync(cancellationToken);
        if (level == LocationPermissionLevel.Denied)
        {
            level = await permissions.RequestWhenInUseAsync(cancellationToken);
            Report(level);
        }

        if (level == LocationPermissionLevel.Denied)
        {
            return LocationAccess.Denied;
        }

        if (!wantsBackground)
        {
            return LocationAccess.Foreground;
        }

        if (level == LocationPermissionLevel.Always)
        {
            return LocationAccess.Background;
        }

        // The system dialog for "Always" comes only after the traveler read why and agreed.
        if (!await rationale.ConfirmAsync(cancellationToken))
        {
            return LocationAccess.Foreground;
        }

        level = await permissions.RequestAlwaysAsync(cancellationToken);
        Report(level);
        return level == LocationPermissionLevel.Always ? LocationAccess.Background : LocationAccess.Foreground;
    }

    private void Report(LocationPermissionLevel level) =>
        analytics.Track("location_permission_result", new Dictionary<string, object?>
        {
            ["level"] = level switch { LocationPermissionLevel.Always => "always", LocationPermissionLevel.WhenInUse => "when_in_use", _ => "denied" },
        });
}

/// <summary>
/// Lets the screen answer the question "pourquoi « Toujours » ?" for <see cref="IBackgroundRationale"/>: <see cref="Pending"/> shows the dialog,
/// <see cref="Answer"/> closes it. Only one question is open at a time.
/// </summary>
public sealed class BackgroundRationaleBroker : IBackgroundRationale
{
    private readonly object _lock = new();
    private TaskCompletionSource<bool>? _pending;

    public event Action? Changed;

    public bool Pending
    {
        get
        {
            lock (_lock)
            {
                return _pending is not null;
            }
        }
    }

    public async Task<bool> ConfirmAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> source;
        lock (_lock)
        {
            source = _pending ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        Changed?.Invoke();
        await using var registration = cancellationToken.Register(() => Answer(false));
        return await source.Task;
    }

    public void Answer(bool agreed)
    {
        TaskCompletionSource<bool>? source;
        lock (_lock)
        {
            source = _pending;
            _pending = null;
        }

        source?.TrySetResult(agreed);
        Changed?.Invoke();
    }
}
