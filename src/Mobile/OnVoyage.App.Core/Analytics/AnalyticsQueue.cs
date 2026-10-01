using System.Diagnostics.CodeAnalysis;
using OnVoyage.App.Core.Audio;
using OnVoyage.Insights.Contracts;

namespace OnVoyage.App.Core.Analytics;

/// <summary>
/// The home-made analytics module of §14.8: no third-party SDK, the events of the §17.3 catalogue, batches of 50 sent to Insights.
/// <list type="bullet">
/// <item>Consent (§16.3). Granted: everything is queued. Refused: only the essential technical events (crash, playback error, GPS loss) are queued.
/// Undecided counts as refused for sending, but the non-essential events of the onboarding are kept in memory and sent if the traveler accepts,
/// thrown away if the traveler refuses. A refusal also removes the non-essential events already waiting.</item>
/// <item>Offline. Events wait (and are saved through <see cref="IAnalyticsStore"/>); a failed attempt backs off exponentially; the same batch,
/// with the same event ids, is sent again, which the server ignores if it had already stored it.</item>
/// <item>Privacy. Only catalogue events and properties go through (<see cref="EventShaping"/>). The session is a random identifier renewed after
/// a pause; there is no device or advertising identifier, and no position.</item>
/// </list>
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Name fixed by the cahier des charges (§14.8, T-619); it is not a collection.")]
public sealed class AnalyticsQueue : IAnalyticsSink, IDisposable
{
    private readonly IAnalyticsTransport _transport;
    private readonly AnalyticsConsent _consent;
    private readonly IAnalyticsStore _store;
    private readonly AnalyticsContext _context;
    private readonly TimeProvider _clock;
    private readonly AnalyticsOptions _options;
    private readonly ITimer _timer;
    private readonly SemaphoreSlim _flushing = new(1, 1);
    private readonly Lock _gate = new();
    private readonly List<EventDto> _queued = [];
    private readonly List<EventDto> _held = [];

    private Guid _session = Guid.CreateVersion7();
    private DateTimeOffset _lastActivity;
    private DateTimeOffset _retryNotBefore;
    private TimeSpan _backoff;
    private bool _loaded;
    private bool _armed;

    public AnalyticsQueue(IAnalyticsTransport transport, AnalyticsConsent consent, IAnalyticsStore store, AnalyticsContext context, TimeProvider clock, AnalyticsOptions? options = null)
    {
        _transport = transport;
        _consent = consent;
        _store = store;
        _context = context;
        _clock = clock;
        _options = options ?? new AnalyticsOptions();
        _lastActivity = clock.GetUtcNow();
        _timer = clock.CreateTimer(_ => _ = FlushQuietlyAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _consent.Changed += OnConsentChanged;
    }

    /// <summary>Events waiting to be sent (not the ones held for an undecided consent).</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _queued.Count;
            }
        }
    }

    /// <summary>Events held in memory until the traveler answers the consent question.</summary>
    public int Held
    {
        get
        {
            lock (_gate)
            {
                return _held.Count;
            }
        }
    }

    public void Track(string name, IReadOnlyDictionary<string, object?> properties)
    {
        if (!EventCatalogue.TryGet(name, out var definition))
        {
            return;
        }

        var now = _clock.GetUtcNow();
        bool schedule;
        lock (_gate)
        {
            if (now - _lastActivity > _options.SessionGap)
            {
                _session = Guid.CreateVersion7();
            }

            _lastActivity = now;
            var item = new EventDto(Guid.CreateVersion7(), name, now, _session, _context.AppVersion, _context.Platform, EventShaping.Shape(definition, properties));
            if (definition.Essential || _consent.State == AnalyticsConsentState.Granted)
            {
                Add(_queued, item, _options.MaxQueued);
            }
            else if (_consent.State == AnalyticsConsentState.Undecided)
            {
                Add(_held, item, _options.MaxHeld);
            } // refused: a non-essential event is never kept

            schedule = _queued.Count > 0;
        }

        if (schedule)
        {
            Arm();
        }
    }

    /// <summary>
    /// Sends what waits, one batch of <see cref="AnalyticsOptions.BatchSize"/> at a time, until the queue is empty or the network fails. Called by the
    /// timer, when a batch is full, and by hosts when the app goes to the background or comes back. Calls that overlap do nothing.
    /// </summary>
    /// <param name="ignoreBackoff">True to try now even if the last attempt failed a moment ago (the app just came back online).</param>
    public async Task FlushAsync(bool ignoreBackoff = false, CancellationToken cancellationToken = default)
    {
        if (!await _flushing.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            await LoadSavedAsync(cancellationToken);
            while (true)
            {
                EventDto[] batch;
                lock (_gate)
                {
                    DropForbidden();
                    if (_queued.Count == 0 || (!ignoreBackoff && _clock.GetUtcNow() < _retryNotBefore))
                    {
                        break;
                    }

                    batch = [.. _queued.Take(_options.BatchSize)];
                }

                var outcome = await _transport.SendAsync(batch, cancellationToken);
                if (outcome == AnalyticsSendOutcome.Retry)
                {
                    BackOff();
                    break;
                }

                lock (_gate)
                {
                    var sent = batch.Select(e => e.Id).ToHashSet();
                    _queued.RemoveAll(e => sent.Contains(e.Id));
                    _backoff = TimeSpan.Zero;
                    _retryNotBefore = default;
                }

                await SaveAsync(cancellationToken);
            }

            await SaveAsync(cancellationToken);
        }
        finally
        {
            _flushing.Release();
            bool more;
            lock (_gate)
            {
                more = _queued.Count > 0;
            }

            if (more)
            {
                Arm();
            }
        }
    }

    public void Dispose()
    {
        _consent.Changed -= OnConsentChanged;
        _timer.Dispose();
        _flushing.Dispose();
    }

    private static void Add(List<EventDto> list, EventDto item, int max)
    {
        list.Add(item);
        if (list.Count > max)
        {
            list.RemoveRange(0, list.Count - max);
        }
    }

    /// <summary>
    /// Plans a flush: after <see cref="AnalyticsOptions.FlushAfter"/> for the first waiting event (later events do not push it back), at once when a
    /// batch is full, never before the end of a backoff.
    /// </summary>
    private void Arm()
    {
        TimeSpan wait;
        lock (_gate)
        {
            var full = _queued.Count >= _options.BatchSize;
            if (_armed && !full)
            {
                return;
            }

            wait = full ? TimeSpan.Zero : _options.FlushAfter;
            var untilRetry = _retryNotBefore - _clock.GetUtcNow();
            if (untilRetry > wait)
            {
                wait = untilRetry;
            }

            _armed = true;
        }

        _timer.Change(wait, Timeout.InfiniteTimeSpan);
    }

    private async Task FlushQuietlyAsync()
    {
        lock (_gate)
        {
            _armed = false;
        }

        try
        {
            await FlushAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Analytics must never take the app down; the events stay queued and the next trigger tries again.
        }
    }

    private void BackOff()
    {
        lock (_gate)
        {
            _backoff = _backoff == TimeSpan.Zero ? TimeSpan.FromSeconds(30) : TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, _options.MaxBackoff.Ticks));
            _retryNotBefore = _clock.GetUtcNow() + _backoff;
        }
    }

    private void OnConsentChanged(AnalyticsConsentState state)
    {
        lock (_gate)
        {
            switch (state)
            {
                case AnalyticsConsentState.Granted:
                    _queued.AddRange(_held);
                    _held.Clear();
                    _queued.Sort((a, b) => a.OccurredAt.CompareTo(b.OccurredAt));
                    break;
                case AnalyticsConsentState.Refused:
                    _held.Clear();
                    _queued.RemoveAll(e => !EventCatalogue.IsEssential(e.Name));
                    break;
                default: // back to undecided: what waits for the network goes back to waiting for the answer
                    var withdrawn = _queued.Where(e => !EventCatalogue.IsEssential(e.Name)).ToList();
                    _queued.RemoveAll(e => !EventCatalogue.IsEssential(e.Name));
                    _held.AddRange(withdrawn);
                    break;
            }
        }

        _ = SaveQuietlyAsync();
        bool any;
        lock (_gate)
        {
            any = _queued.Count > 0;
        }

        if (any)
        {
            Arm();
        }
    }

    /// <summary>Last line of defence before a send: a non-essential event never goes out unless the consent is granted right now.</summary>
    private void DropForbidden()
    {
        if (_consent.State != AnalyticsConsentState.Granted)
        {
            _queued.RemoveAll(e => !EventCatalogue.IsEssential(e.Name));
        }
    }

    private async Task LoadSavedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        var saved = await _store.LoadAsync(cancellationToken);
        lock (_gate)
        {
            var known = _queued.Select(e => e.Id).ToHashSet();
            _queued.InsertRange(0, saved.Where(e => known.Add(e.Id) && EventCatalogue.TryGet(e.Name, out _)));
            if (_queued.Count > _options.MaxQueued)
            {
                _queued.RemoveRange(0, _queued.Count - _options.MaxQueued);
            }

            _loaded = true;
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        EventDto[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _queued];
        }

        await _store.SaveAsync(snapshot, cancellationToken);
    }

    private async Task SaveQuietlyAsync()
    {
        try
        {
            await SaveAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best effort: the in-memory queue stays the reference.
        }
    }
}
