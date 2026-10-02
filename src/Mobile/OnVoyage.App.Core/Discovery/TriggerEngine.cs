namespace OnVoyage.App.Core.Discovery;

/// <summary>
/// The discovery mode's brain (§14.5): pure C#, driven only by the positions it is given and their timestamps, never by the wall clock,
/// so a recorded GPX trace replays exactly. It decides <i>when</i> to tell which story; it plays nothing and stores nothing.
/// </summary>
public sealed class TriggerEngine(TriggerSettings settings, ITriggerHistory history)
{
    private const double KilometresPerHourToMetresPerSecond = 1d / 3.6d;
    private const double MinHeadingDisplacementMeters = 8d;

    private readonly TriggerSettings _settings = settings;
    private readonly ITriggerHistory _history = history;
    private readonly List<LocationFix> _recent = [];
    private readonly List<double> _speeds = [];
    private readonly Dictionary<Guid, DateTimeOffset> _toldThisSession = [];
    private readonly Dictionary<Guid, VisitTrack> _visits = [];
    private readonly HashSet<Guid> _excluded = [];

    private IReadOnlyList<TriggerCandidate> _candidates = [];
    private LocationFix? _lastValid;
    private double? _heading;
    private bool _modeEstablished;
    private TravelMode? _pendingMode;
    private DateTimeOffset _pendingSince;
    private (double Latitude, double Longitude, DateTimeOffset Since)? _anchor;
    private DateTimeOffset _cooldownUntil;
    private bool _inCall;
    private bool _externalPlayback;

    public EngineState State { get; private set; } = EngineState.Off;

    public TravelMode Mode { get; private set; } = TravelMode.Walk;

    /// <summary>True while no valid position has arrived for the signal-loss delay. The engine never extrapolates: it simply waits.</summary>
    public bool SignalLost { get; private set; }

    public double SmoothedSpeedMetersPerSecond { get; private set; }

    public void Start()
    {
        State = EngineState.Listening;
        Reset();
    }

    /// <summary>Stops listening and returns the visits that were still going on.</summary>
    public IReadOnlyList<Visit> Stop()
    {
        var finished = CloseVisits();
        State = EngineState.Off;
        Reset();
        return finished;
    }

    public void SetCandidates(IReadOnlyList<TriggerCandidate> candidates) => _candidates = candidates;

    /// <summary>Places the traveler asked not to hear about again (§F-13); never triggered.</summary>
    public void SetExcluded(IEnumerable<Guid> poiIds)
    {
        _excluded.Clear();
        _excluded.UnionWith(poiIds);
    }

    public void OnCallStateChanged(bool inCall) => _inCall = inCall;

    /// <summary>The traveler started a story by hand: discovery waits until it is over instead of talking over it.</summary>
    public void SetExternalPlayback(bool playing) => _externalPlayback = playing;

    /// <summary>The announcement finished and the story itself starts.</summary>
    public void OnStoryStarted()
    {
        if (State == EngineState.Announcing)
        {
            State = EngineState.Playing;
        }
    }

    /// <summary>The story ended (or the traveler stopped it): the minimum gap before the next one starts now.</summary>
    public void OnPlaybackEnded(DateTimeOffset at)
    {
        if (State is EngineState.Announcing or EngineState.Playing)
        {
            State = EngineState.Cooldown;
            _cooldownUntil = at + TimeSpan.FromSeconds(_settings.MinGapSeconds);
        }
    }

    /// <summary>Called on a timer while no position arrives, so the state shows a lost signal.</summary>
    public void OnTick(DateTimeOffset now)
    {
        SignalLost = State != EngineState.Off && _lastValid is { } last && (now - last.Timestamp).TotalSeconds > _settings.GpsLossSeconds;
    }

    /// <summary>True when the traveler has not moved for long enough that discovery mode should switch itself off (annexe E <c>auto_stop_idle_hours</c>).</summary>
    public bool IdleTooLong(DateTimeOffset now) => _anchor is { } anchor && (now - anchor.Since).TotalHours >= _settings.AutoStopIdleHours;

    public TriggerOutcome OnFix(LocationFix fix)
    {
        if (State == EngineState.Off)
        {
            return Outcome(null, [], SkipReason.Off);
        }

        if (_lastValid is { } previous && fix.Timestamp <= previous.Timestamp)
        {
            return Outcome(null, [], SkipReason.OutOfOrder);
        }

        if (fix.AccuracyMeters > _settings.MaxAccuracyFor(Mode) || double.IsNaN(fix.AccuracyMeters))
        {
            return Outcome(null, [], SkipReason.InaccurateFix);
        }

        List<Visit> visits = [];
        if (_lastValid is { } last && (fix.Timestamp - last.Timestamp).TotalSeconds > _settings.GpsLossSeconds)
        {
            // After a gap nothing is known about the way: drop the speed history and end the visits at the last position actually seen.
            visits.AddRange(CloseVisits());
            _recent.Clear();
            _speeds.Clear();
            _pendingMode = null;
            _anchor = null;
        }

        _lastValid = fix;
        SignalLost = false;
        _recent.Add(fix);
        _recent.RemoveAll(item => (fix.Timestamp - item.Timestamp).TotalSeconds > 120);

        UpdateSpeed(fix);
        UpdateHeading(fix);
        UpdateMode(fix.Timestamp);
        UpdateAnchor(fix);
        visits.AddRange(UpdateVisits(fix));

        if (State == EngineState.Cooldown && fix.Timestamp >= _cooldownUntil)
        {
            State = EngineState.Listening;
        }

        if (State != EngineState.Listening)
        {
            return Outcome(null, visits, SkipReason.Busy);
        }

        if (!_modeEstablished)
        {
            return Outcome(null, visits, SkipReason.WarmingUp);
        }

        if (_inCall)
        {
            return Outcome(null, visits, SkipReason.InCall);
        }

        if (_externalPlayback)
        {
            return Outcome(null, visits, SkipReason.Busy);
        }

        if (_anchor is { } anchor && (fix.Timestamp - anchor.Since).TotalMinutes >= _settings.StationaryStopMinutes)
        {
            return Outcome(null, visits, SkipReason.Immobile);
        }

        var trigger = Select(fix);
        if (trigger is null)
        {
            return Outcome(null, visits, SkipReason.NoCandidate);
        }

        _toldThisSession[trigger.PoiId] = fix.Timestamp;
        State = EngineState.Announcing;
        return Outcome(trigger, visits, SkipReason.None);
    }

    private TriggerOutcome Outcome(Trigger? trigger, IReadOnlyList<Visit> visits, SkipReason reason) => new(Mode, State, trigger, visits, reason);

    private void Reset()
    {
        _recent.Clear();
        _speeds.Clear();
        _visits.Clear();
        _toldThisSession.Clear();
        _lastValid = null;
        _heading = null;
        _modeEstablished = false;
        _pendingMode = null;
        _anchor = null;
        _inCall = false;
        _externalPlayback = false;
        SignalLost = false;
        SmoothedSpeedMetersPerSecond = 0d;
        Mode = TravelMode.Walk;
        _cooldownUntil = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Speed comes from the device when it gives one, otherwise from the displacement over a baseline of several seconds: dividing the
    /// jump between two noisy positions a second apart would make a standing traveler look like a runner.
    /// </summary>
    private void UpdateSpeed(LocationFix fix)
    {
        double? speed = null;
        if (fix.SpeedMetersPerSecond is { } reported and >= 0 && !double.IsNaN(reported))
        {
            speed = reported;
        }
        else
        {
            var reference = _recent.LastOrDefault(item => (fix.Timestamp - item.Timestamp).TotalSeconds >= _settings.SpeedBaselineSeconds);
            if (reference.Timestamp != default)
            {
                var seconds = (fix.Timestamp - reference.Timestamp).TotalSeconds;
                speed = GeoMath.DistanceMeters(reference.Latitude, reference.Longitude, fix.Latitude, fix.Longitude) / seconds;
            }
        }

        if (speed is { } value)
        {
            _speeds.Add(value);
            if (_speeds.Count > Math.Max(1, _settings.SpeedMedianWindow))
            {
                _speeds.RemoveAt(0);
            }
        }

        SmoothedSpeedMetersPerSecond = GeoMath.Median(_speeds);
    }

    private void UpdateHeading(LocationFix fix)
    {
        if (fix.HeadingDegrees is { } reported && !double.IsNaN(reported) && SmoothedSpeedMetersPerSecond >= 0.5d)
        {
            _heading = (reported % 360d + 360d) % 360d;
            return;
        }

        var reference = _recent.LastOrDefault(item => (fix.Timestamp - item.Timestamp).TotalSeconds >= 8d);
        if (reference.Timestamp != default && GeoMath.DistanceMeters(reference.Latitude, reference.Longitude, fix.Latitude, fix.Longitude) >= MinHeadingDisplacementMeters)
        {
            _heading = GeoMath.BearingDegrees(reference.Latitude, reference.Longitude, fix.Latitude, fix.Longitude);
        }
    }

    private TravelMode ModeFor(double metersPerSecond)
    {
        var kmh = metersPerSecond * 3.6d;
        return kmh > _settings.CarMinSpeedKmh ? TravelMode.Car : kmh >= _settings.BikeMinSpeedKmh ? TravelMode.Bike : TravelMode.Walk;
    }

    /// <summary>The mode changes only after the new one has been steady for the hysteresis delay; the very first estimate applies at once.</summary>
    private void UpdateMode(DateTimeOffset now)
    {
        if (_speeds.Count == 0)
        {
            return;
        }

        var desired = ModeFor(SmoothedSpeedMetersPerSecond);
        if (!_modeEstablished && _speeds.Count >= 3)
        {
            Mode = desired;
            _modeEstablished = true;
            return;
        }

        if (desired == Mode)
        {
            _pendingMode = null;
            return;
        }

        if (_pendingMode != desired)
        {
            _pendingMode = desired;
            _pendingSince = now;
        }
        else if ((now - _pendingSince).TotalSeconds >= _settings.ModeSwitchSeconds)
        {
            Mode = desired;
            _pendingMode = null;
        }
    }

    /// <summary>The anchor is where the traveler has been standing since; it moves when they walk away from it.</summary>
    private void UpdateAnchor(LocationFix fix)
    {
        if (_anchor is not { } anchor || GeoMath.DistanceMeters(anchor.Latitude, anchor.Longitude, fix.Latitude, fix.Longitude) > _settings.StationaryRadiusMeters)
        {
            _anchor = (fix.Latitude, fix.Longitude, fix.Timestamp);
        }
    }

    private List<Visit> UpdateVisits(LocationFix fix)
    {
        List<Visit> finished = [];
        var slow = SmoothedSpeedMetersPerSecond <= _settings.VisitMaxSpeedKmh * KilometresPerHourToMetresPerSecond;
        HashSet<Guid> here = [];
        if (slow)
        {
            foreach (var candidate in _candidates)
            {
                var radius = Math.Max(_settings.VisitRadiusMeters, candidate.FootprintRadiusMeters);
                if (GeoMath.DistanceMeters(fix.Latitude, fix.Longitude, candidate.Latitude, candidate.Longitude) <= radius)
                {
                    here.Add(candidate.PoiId);
                    if (!_visits.TryGetValue(candidate.PoiId, out var track))
                    {
                        track = new VisitTrack(fix.Timestamp);
                        _visits[candidate.PoiId] = track;
                    }

                    track.LastSeen = fix.Timestamp;
                    track.Accuracies.Add(fix.AccuracyMeters);
                }
            }
        }

        foreach (var poiId in _visits.Keys.Where(id => !here.Contains(id)).ToList())
        {
            if (TryFinish(poiId, _visits[poiId], requireDwell: true) is { } visit)
            {
                finished.Add(visit);
            }

            _visits.Remove(poiId);
        }

        return finished;
    }

    private List<Visit> CloseVisits()
    {
        List<Visit> finished = [];
        foreach (var (poiId, track) in _visits)
        {
            if (TryFinish(poiId, track, requireDwell: true) is { } visit)
            {
                finished.Add(visit);
            }
        }

        _visits.Clear();
        return finished;
    }

    private Visit? TryFinish(Guid poiId, VisitTrack track, bool requireDwell)
    {
        var dwell = track.LastSeen - track.StartedAt;
        if (requireDwell && dwell.TotalMinutes < _settings.VisitMinMinutes)
        {
            return null;
        }

        var accuracy = GeoMath.Median(track.Accuracies);
        var precision = accuracy <= 20d ? 1d : accuracy <= 50d ? 0.8d : 0.5d;
        var confidence = Math.Min(1d, dwell.TotalMinutes / _settings.VisitFullMinutes) * precision;
        return new Visit(poiId, track.StartedAt, dwell, Math.Round(confidence, 3));
    }

    /// <summary>The filters that do not depend on the distance: the same list for the trigger and for the "next story" shown in car mode.</summary>
    private bool IsEligible(TriggerCandidate candidate, LocationFix fix)
    {
        if (candidate.Fragile || candidate.StoryId == Guid.Empty || _excluded.Contains(candidate.PoiId))
        {
            return false;
        }

        if (Mode == TravelMode.Car
            ? candidate.Importance < _settings.CarMinImportance && !candidate.VisibleFromRoad && !candidate.CarAccessible
            : candidate.Importance < _settings.MinImportanceFor(Mode))
        {
            return false;
        }

        var told = TimeSpan.FromDays(_settings.RepeatAfterDays);
        var lastTold = Latest(_history.LastTold(candidate.PoiId), _toldThisSession.TryGetValue(candidate.PoiId, out var session) ? session : null);
        return lastTold is not { } at || fix.Timestamp - at >= told;
    }

    /// <summary>False when the place is behind the traveler (bike and car). Without a known heading the question cannot be answered, so nothing is guessed.</summary>
    private bool IsAhead(double bearing)
    {
        if (Mode == TravelMode.Walk)
        {
            return true;
        }

        if (_heading is not { } heading)
        {
            return false;
        }

        var cone = Mode == TravelMode.Bike ? _settings.BikeConeDegrees : _settings.CarConeDegrees;
        return Math.Abs(GeoMath.SignedAngleDegrees(heading, bearing)) <= cone;
    }

    private double TriggerScore(TriggerCandidate candidate)
    {
        var crowdPenalty = Math.Clamp((candidate.CrowdLevel - 1) / 4d, 0d, 1d);
        return candidate.BaseScore + (_settings.ContextWeight * candidate.ContextScore) - (_settings.CrowdWeight * crowdPenalty);
    }

    /// <summary>
    /// The place the engine would announce next if the traveler kept going: the nearest eligible one ahead within the look-ahead distance,
    /// whatever the trigger radius. For the car-mode screen ("prochaine histoire et sa distance"); it triggers nothing. Null before the first position.
    /// </summary>
    public UpcomingStory? Upcoming()
    {
        if (State == EngineState.Off || _lastValid is not { } fix || !_modeEstablished)
        {
            return null;
        }

        UpcomingStory? nearest = null;
        var nearestDistance = double.MaxValue;
        foreach (var candidate in _candidates)
        {
            if (!IsEligible(candidate, fix) || TriggerScore(candidate) < _settings.MinTriggerScore)
            {
                continue;
            }

            var distance = GeoMath.DistanceMeters(fix.Latitude, fix.Longitude, candidate.Latitude, candidate.Longitude);
            if (distance > _settings.UpcomingLookaheadMeters || distance >= nearestDistance)
            {
                continue;
            }

            if (!IsAhead(GeoMath.BearingDegrees(fix.Latitude, fix.Longitude, candidate.Latitude, candidate.Longitude)))
            {
                continue;
            }

            nearestDistance = distance;
            nearest = new UpcomingStory(candidate.PoiId, candidate.Name, RoundTo50(distance));
        }

        return nearest;
    }

    private Trigger? Select(LocationFix fix)
    {
        var speed = SmoothedSpeedMetersPerSecond;
        var radius = _settings.RadiusFor(Mode, speed);
        Trigger? best = null;
        var bestRank = double.MinValue;

        foreach (var candidate in _candidates)
        {
            if (!IsEligible(candidate, fix))
            {
                continue;
            }

            var distance = GeoMath.DistanceMeters(fix.Latitude, fix.Longitude, candidate.Latitude, candidate.Longitude);
            var reach = Mode == TravelMode.Car ? Math.Min(radius, Math.Max(speed, 1d) * _settings.CarAnticipationSeconds) : radius;
            if (distance > reach)
            {
                continue;
            }

            var bearing = GeoMath.BearingDegrees(fix.Latitude, fix.Longitude, candidate.Latitude, candidate.Longitude);
            if (!IsAhead(bearing))
            {
                continue;
            }

            var score = TriggerScore(candidate);
            if (score < _settings.MinTriggerScore)
            {
                continue;
            }

            var rank = score * (1d - (distance / radius));
            if (rank <= bestRank)
            {
                continue;
            }

            bestRank = rank;
            best = new Trigger(candidate.PoiId, Mode, RoundTo50(distance), DirectionOf(bearing), Math.Round(score, 3), Mode == TravelMode.Car, candidate.StoryId);
        }

        return best;
    }

    private AnnouncementDirection DirectionOf(double bearing)
    {
        if (_heading is not { } heading)
        {
            return AnnouncementDirection.Front;
        }

        var relative = GeoMath.SignedAngleDegrees(heading, bearing);
        return Math.Abs(relative) <= _settings.FrontConeDegrees ? AnnouncementDirection.Front : relative > 0 ? AnnouncementDirection.Right : AnnouncementDirection.Left;
    }

    private static int RoundTo50(double meters) => (int)(Math.Round(meters / 50d, MidpointRounding.AwayFromZero) * 50d);

    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;

    private sealed class VisitTrack(DateTimeOffset startedAt)
    {
        public DateTimeOffset StartedAt { get; } = startedAt;

        public DateTimeOffset LastSeen { get; set; } = startedAt;

        public List<double> Accuracies { get; } = [];
    }
}
