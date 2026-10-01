namespace OnVoyage.App.Core.Discovery;

/// <summary>The device's position stream while discovery mode is on (foreground in the MVP-0). Positions are given to the engine and dropped.</summary>
public interface ILocationSource
{
    event Action<LocationFix>? FixReceived;

    /// <summary>Asks for the permission if needed and starts listening; false when it was refused or location is off.</summary>
    Task<bool> StartAsync(CancellationToken cancellationToken);

    Task StopAsync();
}

/// <summary>Keeps the screen on while discovery mode runs in the foreground (option "garder l'écran allumé").</summary>
public interface IScreenKeepAwake
{
    void SetAwake(bool awake);
}

public sealed class NoScreenKeepAwake : IScreenKeepAwake
{
    public void SetAwake(bool awake)
    {
    }
}

/// <summary>Tells when a phone call is in progress. Where the platform cannot, <see cref="NoCallMonitor"/> is used and audio interruptions cover it.</summary>
public interface ICallMonitor
{
    event Action<bool>? CallStateChanged;
}

public sealed class NoCallMonitor : ICallMonitor
{
    public event Action<bool>? CallStateChanged
    {
        add { }
        remove { }
    }
}

/// <summary>When each place was told, kept on the device so the 30-day delay survives a restart.</summary>
public interface ITellHistoryStore
{
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> LoadAsync(CancellationToken cancellationToken);

    Task MarkAsync(Guid poiId, DateTimeOffset at, CancellationToken cancellationToken);
}

public sealed class InMemoryTellHistoryStore : ITellHistoryStore
{
    private readonly Dictionary<Guid, DateTimeOffset> _told = [];

    public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, DateTimeOffset>>(new Dictionary<Guid, DateTimeOffset>(_told));

    public Task MarkAsync(Guid poiId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        _told[poiId] = at;
        return Task.CompletedTask;
    }
}

/// <summary>Where a detected stay goes (the profile's "visited" state and the sync queue). Visits carry no coordinates.</summary>
public interface IVisitSink
{
    Task RecordAsync(Visit visit, CancellationToken cancellationToken);
}

public sealed class NullVisitSink : IVisitSink
{
    public Task RecordAsync(Visit visit, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The remote <c>trigger</c> settings when they were fetched, the shipped defaults otherwise.</summary>
public interface ITriggerSettingsProvider
{
    TriggerSettings Current { get; }
}

public sealed class DefaultTriggerSettingsProvider : ITriggerSettingsProvider
{
    public TriggerSettings Current { get; } = new();
}

/// <summary>A scripted position source: tests and the in-app "simulate a walk" feature push fixes by hand.</summary>
public sealed class SimulatedLocationSource : ILocationSource
{
    public event Action<LocationFix>? FixReceived;

    public bool Started { get; private set; }

    public bool Permitted { get; set; } = true;

    public Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        Started = Permitted;
        return Task.FromResult(Permitted);
    }

    public Task StopAsync()
    {
        Started = false;
        return Task.CompletedTask;
    }

    public void Emit(LocationFix fix)
    {
        if (Started)
        {
            FixReceived?.Invoke(fix);
        }
    }
}
