using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.Core.Feedback;
using OnVoyage.App.Core.Profile;

namespace OnVoyage.App.Core.Wishes;

/// <summary>
/// Foreground reminders (MVP-0, F-08): while the position stream runs (discovery mode), a saved place nearby becomes a local reminder. The
/// position is read in memory and dropped; only the date of the last reminder is kept, on the device.
/// </summary>
public sealed class ProximityReminderService(
    ILocationSource location,
    IProfileStore profiles,
    ICatalogClient catalog,
    IReminderStore store,
    ILocalNotifier notifier,
    TimeProvider clock) : IDisposable
{
    private static readonly TimeSpan EvaluateEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PlacesTtl = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<double> _speeds = [];
    private LocationFix? _previous;
    private DateTimeOffset _lastEvaluation = DateTimeOffset.MinValue;
    private IReadOnlyList<SavedPlace> _places = [];
    private DateTimeOffset _placesAt = DateTimeOffset.MinValue;
    private bool _started;

    public event Action? Changed;

    public Reminder? Current { get; private set; }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        location.FixReceived += OnFix;
    }

    public void Dismiss()
    {
        Current = null;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        location.FixReceived -= OnFix;
        _gate.Dispose();
    }

    internal Task LastHandling { get; private set; } = Task.CompletedTask;

    /// <summary>Lets other test assemblies await the handling of the last position.</summary>
    public Task LastHandlingForTests => LastHandling;

    private void OnFix(LocationFix fix) => LastHandling = HandleAsync(fix);

    private async Task HandleAsync(LocationFix fix)
    {
        await _gate.WaitAsync();
        try
        {
            var speed = fix.SpeedMetersPerSecond ?? (_previous is { } before && fix.Timestamp > before.Timestamp
                ? GeoMath.DistanceMeters(before.Latitude, before.Longitude, fix.Latitude, fix.Longitude) / (fix.Timestamp - before.Timestamp).TotalSeconds
                : 0d);
            _previous = fix;
            _speeds.Add(speed);
            if (_speeds.Count > 5)
            {
                _speeds.RemoveAt(0);
            }

            if (fix.Timestamp - _lastEvaluation < EvaluateEvery)
            {
                return;
            }

            _lastEvaluation = fix.Timestamp;
            var now = clock.GetUtcNow();
            var places = await PlacesAsync(now);
            if (places.Count == 0)
            {
                return;
            }

            var reminder = ProximityReminderRule.Evaluate(fix.Latitude, fix.Longitude, GeoMath.Median(_speeds), places, await store.LoadAsync(CancellationToken.None), now);
            if (reminder is null)
            {
                return;
            }

            await store.MarkAsync(reminder.PoiId, now, CancellationToken.None);
            Current = reminder;
            Changed?.Invoke();
            await notifier.NotifyAsync("Un lieu que vous aimez est tout près", reminder.Message, CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<SavedPlace>> PlacesAsync(DateTimeOffset now)
    {
        if (now - _placesAt < PlacesTtl)
        {
            return _places;
        }

        var profile = await profiles.LoadAsync(CancellationToken.None);
        if (profile.Saved.Count == 0)
        {
            _places = [];
        }
        else
        {
            var pois = await catalog.GetPoisAsync(profile.Destination, null, null, CancellationToken.None);
            _places = [.. pois.Where(poi => profile.Saved.Contains(poi.Id)).Select(poi => new SavedPlace(poi.Id, poi.Name, poi.Slug, poi.Latitude, poi.Longitude, profile.SavedAt.GetValueOrDefault(poi.Id, now)))];
        }

        _placesAt = now;
        return _places;
    }
}
