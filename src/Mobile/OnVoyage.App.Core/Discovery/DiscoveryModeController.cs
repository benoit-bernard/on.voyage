using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Home;
using OnVoyage.App.Core.Profile;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Recommendation.Engine;

namespace OnVoyage.App.Core.Discovery;

public enum DiscoveryStartResult
{
    Started,
    AlreadyOn,
    PermissionDenied,
    NothingToTell,
}

/// <summary>What the "discovery mode" button and the status line show.</summary>
public sealed record DiscoveryState(EngineState Engine, TravelMode Mode, bool SignalLost, string? LastTitle, bool KeepScreenOn)
{
    public static DiscoveryState Off { get; } = new(EngineState.Off, TravelMode.Walk, false, null, false);

    public bool IsOn => Engine != EngineState.Off;
}

/// <summary>
/// Discovery mode in the foreground (F-09, T-611). The traveler turns it on; from then on positions feed the <see cref="TriggerEngine"/>,
/// and a trigger becomes a story played through the <see cref="AudioPlaybackController"/>: jingle, direction, then the story. The
/// positions stay in memory; what leaves this class is a trigger (a rounded distance), a visit (a place and a duration) and an event.
/// </summary>
public sealed class DiscoveryModeController(
    ICatalogClient catalog,
    IProfileStore profiles,
    ISessionProvider sessions,
    ILocationSource location,
    AudioPlaybackController audio,
    ITellHistoryStore tellHistory,
    IVisitSink visits,
    IScreenKeepAwake keepAwake,
    ICallMonitor calls,
    ITriggerSettingsProvider settings,
    IAnalyticsSink analytics,
    TimeProvider clock) : IDisposable
{
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Told> _stories = [];
    private TriggerEngine? _engine;
    private InMemoryTriggerHistory? _history;
    private ITimer? _timer;
    private string? _lastTitle;
    private bool _keepScreenOn;
    private bool _subscribed;

    public event Action? Changed;

    public void Dispose()
    {
        _timer?.Dispose();
        Unsubscribe();
        _gate.Dispose();
    }

    public DiscoveryState State { get; private set; } = DiscoveryState.Off;

    /// <summary>Starts discovery. The permission is requested here, never before: the traveler pressed the button.</summary>
    public async Task<DiscoveryStartResult> StartAsync(bool keepScreenOn, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_engine is not null)
            {
                return DiscoveryStartResult.AlreadyOn;
            }

            var profile = await profiles.LoadAsync(cancellationToken);
            var session = await sessions.EnsureSessionAsync(cancellationToken);
            var pois = await catalog.GetPoisAsync(profile.Destination, null, null, cancellationToken);
            var (candidates, stories) = Build(pois, profile with { TravelerId = session.TravelerId });
            if (candidates.Count == 0)
            {
                return DiscoveryStartResult.NothingToTell;
            }

            if (!await location.StartAsync(cancellationToken))
            {
                return DiscoveryStartResult.PermissionDenied;
            }

            _history = new InMemoryTriggerHistory();
            foreach (var (poiId, at) in await tellHistory.LoadAsync(cancellationToken))
            {
                _history.MarkTold(poiId, at);
            }

            _stories.Clear();
            foreach (var (poiId, story) in stories)
            {
                _stories[poiId] = story;
            }

            _engine = new TriggerEngine(settings.Current, _history);
            _engine.SetCandidates(candidates);
            _engine.Start();
            _keepScreenOn = keepScreenOn;
            keepAwake.SetAwake(keepScreenOn);
            Subscribe();
            _timer = clock.CreateTimer(_ => OnTick(), null, TickEvery, TickEvery);
            Publish();
            return DiscoveryStartResult.Started;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        location.FixReceived += OnFix;
        calls.CallStateChanged += OnCallState;
        audio.MainStarted += OnMainStarted;
        audio.StoryEnded += OnStoryEnded;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        location.FixReceived -= OnFix;
        calls.CallStateChanged -= OnCallState;
        audio.MainStarted -= OnMainStarted;
        audio.StoryEnded -= OnStoryEnded;
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_engine is null)
        {
            return;
        }

        _timer?.Dispose();
        _timer = null;
        Unsubscribe();
        await location.StopAsync();
        keepAwake.SetAwake(false);
        var finished = _engine.Stop();
        _engine = null;
        _history = null;
        foreach (var visit in finished)
        {
            await visits.RecordAsync(visit, cancellationToken);
        }

        State = DiscoveryState.Off;
        Changed?.Invoke();
    }

    /// <summary>The last position being handled; tests await it, the app never needs to.</summary>
    internal Task LastHandling { get; private set; } = Task.CompletedTask;

    private void OnFix(LocationFix fix) => LastHandling = HandleFixAsync(fix);

    private async Task HandleFixAsync(LocationFix fix)
    {
        await _gate.WaitAsync();
        try
        {
            if (_engine is null)
            {
                return;
            }

            _engine.SetExternalPlayback(audio.State.Current is { Origin: PlayOrigin.Manual });
            var outcome = _engine.OnFix(fix);
            foreach (var visit in outcome.Visits)
            {
                await visits.RecordAsync(visit, CancellationToken.None);
            }

            if (outcome.Trigger is { } trigger && _stories.TryGetValue(trigger.PoiId, out var story))
            {
                analytics.Track("story_triggered", new Dictionary<string, object?>
                {
                    ["poi_id"] = trigger.PoiId.ToString(),
                    ["mode"] = trigger.Mode.ToString().ToLowerInvariant(),
                    ["distance_m"] = trigger.DistanceMeters,
                });
                _lastTitle = story.Title;
                await audio.PlayNowAsync(story.ToRequest(trigger.Direction, trigger.PoiId));
            }

            Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnMainStarted(PlayRequest request)
    {
        if (request.Origin != PlayOrigin.Discovery || _engine is null)
        {
            return;
        }

        _engine.OnStoryStarted();
        _history?.MarkTold(request.PoiId, clock.GetUtcNow());
        _ = tellHistory.MarkAsync(request.PoiId, clock.GetUtcNow(), CancellationToken.None);
        Publish();
    }

    private void OnStoryEnded(PlayRequest request, bool completed)
    {
        _ = completed;
        if (request.Origin != PlayOrigin.Discovery || _engine is null)
        {
            return;
        }

        _engine.OnPlaybackEnded(clock.GetUtcNow());
        Publish();
    }

    private void OnCallState(bool inCall) => _engine?.OnCallStateChanged(inCall);

    private void OnTick()
    {
        var engine = _engine;
        if (engine is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        engine.OnTick(now);
        if (engine.IdleTooLong(now))
        {
            _ = StopAsync();
            return;
        }

        Publish();
    }

    private void Publish()
    {
        if (_engine is { } engine)
        {
            State = new DiscoveryState(engine.State, engine.Mode, engine.SignalLost, _lastTitle, _keepScreenOn);
            Changed?.Invoke();
        }
    }

    /// <summary>Turns the catalog's places and the traveler's taste into what the engine needs, and keeps the audio addresses of each story.</summary>
    internal static (IReadOnlyList<TriggerCandidate> Candidates, IReadOnlyDictionary<Guid, Told> Stories) Build(IReadOnlyList<PoiSummaryDto> pois, LocalProfile profile)
    {
        var withStory = pois.Where(poi => poi.StoryId is not null && poi.AudioParts is { } parts && parts.ContainsKey("main")).ToList();
        var control = ControlCohort.Contains(profile.TravelerId);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var recommendation = withStory.Select(HomeFeedService.ToCandidate).ToArray();
        if (!control)
        {
            foreach (var scored in Recommender.Rank(HomeFeedService.ToTaste(profile), recommendation, Recommendation.Engine.TravelMode.Walk))
            {
                scores[scored.Candidate.Id] = scored.Score;
            }
        }

        List<TriggerCandidate> candidates = [];
        Dictionary<Guid, Told> stories = [];
        foreach (var poi in withStory)
        {
            var baseScore = control ? poi.Importance : scores.GetValueOrDefault(poi.Id.ToString("N"), poi.Importance);
            candidates.Add(new TriggerCandidate(
                poi.Id, poi.Name, poi.Latitude, poi.Longitude, (int)Math.Round(poi.Importance * 100d), Math.Clamp(baseScore, 0d, 1d), poi.CrowdLevel,
                poi.Fragile, VisibleFromRoad: false, CarAccessible: false, poi.StoryId!.Value));
            stories[poi.Id] = new Told(poi.StoryId.Value, poi.Name, poi.AudioParts!);
        }

        return (candidates, stories);
    }

    /// <summary>The audio of one story, ready to become a <see cref="PlayRequest"/> once the direction is known.</summary>
    internal sealed record Told(Guid StoryId, string Title, IReadOnlyDictionary<string, string> Parts)
    {
        public PlayRequest ToRequest(AnnouncementDirection direction, Guid poiId)
        {
            List<AudioSource> sources = [new AudioSource("asset://jingle", AudioRole.Jingle)];
            var announcement = direction switch { AnnouncementDirection.Left => "announce_left", AnnouncementDirection.Right => "announce_right", _ => "announce_front" };
            if (Parts.TryGetValue(announcement, out var url))
            {
                sources.Add(new AudioSource(url, AudioRole.Announcement));
            }

            sources.Add(new AudioSource(Parts["main"], AudioRole.Main));
            return new PlayRequest(StoryId, poiId, Title, sources, PlayOrigin.Discovery);
        }
    }
}
