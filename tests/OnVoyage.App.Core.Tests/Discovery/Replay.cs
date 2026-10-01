using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using OnVoyage.App.Core.Discovery;

namespace OnVoyage.App.Core.Tests.Discovery;

/// <summary>Reads the tracks of <c>data-pipeline/gpx</c>: position, time and (as a GPX extension) the accuracy the device reported.</summary>
internal static class GpxReader
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<LocationFix> Read(string name)
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "gpx", $"{name}.gpx"));
        XNamespace ns = "http://www.topografix.com/GPX/1/1";
        return [.. document.Descendants(ns + "trkpt").Select(point => new LocationFix(
            double.Parse(point.Attribute("lat")!.Value, CultureInfo.InvariantCulture),
            double.Parse(point.Attribute("lon")!.Value, CultureInfo.InvariantCulture),
            double.Parse(point.Descendants(ns + "accuracy").FirstOrDefault()?.Value ?? "10", CultureInfo.InvariantCulture),
            null,
            null,
            DateTimeOffset.Parse(point.Element(ns + "time")!.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)))];
    }

    public static IReadOnlyList<Place> ReadPlaces()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "gpx", "candidates.json"));
        return JsonSerializer.Deserialize<List<Place>>(stream, Web)!;
    }

    public sealed record Place(
        Guid PoiId, string Name, double Latitude, double Longitude, int Importance, double BaseScore, int CrowdLevel, bool Fragile, bool VisibleFromRoad, bool CarAccessible, Guid StoryId, int StorySeconds)
    {
        public TriggerCandidate ToCandidate() => new(PoiId, Name, Latitude, Longitude, Importance, BaseScore, CrowdLevel, Fragile, VisibleFromRoad, CarAccessible, StoryId);
    }
}

internal sealed record ReplayedTrigger(double AtSeconds, string Place, TravelMode Mode, int DistanceMeters, AnnouncementDirection Direction, bool Anticipated, double FixAccuracy, double StoryStartsAtSeconds, double EndsAtSeconds);

internal sealed record ReplayedVisit(string Place, double StartedAtSeconds, double DwellSeconds, double Confidence);

internal sealed record ReplayResult(
    IReadOnlyList<ReplayedTrigger> Triggers, IReadOnlyList<ReplayedVisit> Visits, int RejectedFixes, bool SignalWasLost, IReadOnlyList<(double From, double To)> Gaps);

/// <summary>
/// Replays a track on a virtual clock (§20.4). A triggered story "plays" for an announcement (8 s: jingle and direction) plus its duration,
/// then the engine is told it ended. Ticks run every second, so a gap in the track is seen as a lost signal and not as a pause.
/// </summary>
internal static class Replay
{
    public const double AnnouncementSeconds = 8;

    public static ReplayResult Run(string track, TriggerSettings? settings = null, Func<GpxReader.Place, bool>? only = null, InMemoryTriggerHistory? history = null)
    {
        var fixes = GpxReader.Read(track);
        var places = GpxReader.ReadPlaces().Where(place => only?.Invoke(place) ?? true).ToList();
        var byId = places.ToDictionary(place => place.PoiId);
        history ??= new InMemoryTriggerHistory();
        var engine = new TriggerEngine(settings ?? new TriggerSettings(), history);
        engine.SetCandidates([.. places.Select(place => place.ToCandidate())]);
        engine.Start();

        var origin = fixes[0].Timestamp;
        List<ReplayedTrigger> triggers = [];
        List<ReplayedVisit> visits = [];
        List<(double, double)> gaps = [];
        var rejected = 0;
        var signalLost = false;
        DateTimeOffset? storyStartsAt = null;
        DateTimeOffset? endsAt = null;
        var previous = origin;

        double Seconds(DateTimeOffset at) => (at - origin).TotalSeconds;

        void Advance(DateTimeOffset now)
        {
            if (storyStartsAt is { } start && now >= start)
            {
                engine.OnStoryStarted();
                storyStartsAt = null;
            }

            if (endsAt is { } end && now >= end)
            {
                engine.OnPlaybackEnded(end);
                endsAt = null;
            }
        }

        foreach (var fix in fixes)
        {
            for (var tick = previous.AddSeconds(1); tick < fix.Timestamp; tick = tick.AddSeconds(1))
            {
                Advance(tick);
                engine.OnTick(tick);
                signalLost |= engine.SignalLost;
            }

            if ((fix.Timestamp - previous).TotalSeconds > 20)
            {
                gaps.Add((Seconds(previous), Seconds(fix.Timestamp)));
            }

            Advance(fix.Timestamp);
            var outcome = engine.OnFix(fix);
            rejected += outcome.Reason == SkipReason.InaccurateFix ? 1 : 0;
            if (outcome.Reason != SkipReason.InaccurateFix)
            {
                previous = fix.Timestamp;
            }

            foreach (var visit in outcome.Visits)
            {
                visits.Add(new ReplayedVisit(byId[visit.PoiId].Name, Seconds(visit.StartedAt), visit.Dwell.TotalSeconds, visit.Confidence));
            }

            if (outcome.Trigger is { } trigger)
            {
                var place = byId[trigger.PoiId];
                storyStartsAt = fix.Timestamp.AddSeconds(AnnouncementSeconds);
                endsAt = storyStartsAt.Value.AddSeconds(place.StorySeconds);
                history.MarkTold(trigger.PoiId, fix.Timestamp);
                triggers.Add(new ReplayedTrigger(
                    Math.Round(Seconds(fix.Timestamp), 1), place.Name, trigger.Mode, trigger.DistanceMeters, trigger.Direction, trigger.Anticipated, fix.AccuracyMeters,
                    Math.Round(Seconds(storyStartsAt.Value), 1), Math.Round(Seconds(endsAt.Value), 1)));
            }
        }

        foreach (var visit in engine.Stop())
        {
            visits.Add(new ReplayedVisit(byId[visit.PoiId].Name, Seconds(visit.StartedAt), visit.Dwell.TotalSeconds, visit.Confidence));
        }

        return new ReplayResult(triggers, visits, rejected, signalLost, gaps);
    }
}

/// <summary>
/// "Approved results" (§14.5): the list of triggers of each replay is committed in <c>Approved/</c>. A change in behaviour shows up as a
/// diff to review; run the tests with <c>UPDATE_APPROVED=1</c> to rewrite the files after having read it.
/// </summary>
internal static class Approvals
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static void Verify(string name, object value, [CallerFilePath] string callerFile = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(callerFile)!, "Approved", $"{name}.approved.json");
        var actual = JsonSerializer.Serialize(value, Options).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        if (Environment.GetEnvironmentVariable("UPDATE_APPROVED") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        File.Exists(path).ShouldBeTrue($"No approved result for {name}. Run the tests with UPDATE_APPROVED=1, read the file, commit it.");
        actual.ShouldBe(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal), $"The replay of {name} changed. If that is intended, rerun with UPDATE_APPROVED=1 and review the diff.");
    }
}
