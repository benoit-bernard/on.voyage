using System.Text.Json;

namespace OnVoyage.App.Core.Discovery;

public enum TravelMode
{
    Walk,
    Bike,
    Car,
}

/// <summary><c>Off → Listening → Announcing → Playing → Cooldown → Listening</c> (§14.5).</summary>
public enum EngineState
{
    Off,
    Listening,
    Announcing,
    Playing,
    Cooldown,
}

public enum AnnouncementDirection
{
    Front,
    Left,
    Right,
}

/// <summary>Why a position produced no trigger. For diagnostics and tests; never sent anywhere.</summary>
public enum SkipReason
{
    None,
    Off,
    InaccurateFix,
    OutOfOrder,
    SignalLost,
    /// <summary>The first positions after starting: the travel mode is not known yet, so nothing is announced.</summary>
    WarmingUp,
    Busy,
    InCall,
    Immobile,
    NoCandidate,
}

/// <summary>One position from the device. Only ever used in memory by the engine; a trigger carries a rounded distance, never coordinates.</summary>
public readonly record struct LocationFix(double Latitude, double Longitude, double AccuracyMeters, double? SpeedMetersPerSecond, double? HeadingDegrees, DateTimeOffset Timestamp);

/// <summary>A place the engine may announce, with what the device needs to decide on its own (also offline).</summary>
public sealed record TriggerCandidate(
    Guid PoiId,
    string Name,
    double Latitude,
    double Longitude,
    int Importance,
    double BaseScore,
    int CrowdLevel,
    bool Fragile,
    bool VisibleFromRoad,
    bool CarAccessible,
    Guid StoryId,
    double ContextScore = 0d,
    double FootprintRadiusMeters = 0d);

/// <summary>A story to start now. The analytics event <c>story_triggered</c> is exactly these three fields: no coordinates (D-14).</summary>
public sealed record Trigger(Guid PoiId, TravelMode Mode, int DistanceMeters, AnnouncementDirection Direction, double Score, bool Anticipated, Guid StoryId);

/// <summary>A stay at a place. No coordinates: the place and how long, nothing else (D-14).</summary>
public sealed record Visit(Guid PoiId, DateTimeOffset StartedAt, TimeSpan Dwell, double Confidence);

public sealed record TriggerOutcome(TravelMode Mode, EngineState State, Trigger? Trigger, IReadOnlyList<Visit> Visits, SkipReason Reason);

/// <summary>When each place was last told, so it is not told again for a while.</summary>
public interface ITriggerHistory
{
    DateTimeOffset? LastTold(Guid poiId);
}

public sealed class InMemoryTriggerHistory : ITriggerHistory
{
    private readonly Dictionary<Guid, DateTimeOffset> _told = [];

    public DateTimeOffset? LastTold(Guid poiId) => _told.TryGetValue(poiId, out var at) ? at : null;

    public void MarkTold(Guid poiId, DateTimeOffset at) => _told[poiId] = at;
}

/// <summary>The tunable values of §14.5 (annexe E, key <c>trigger</c>). The defaults are the shipped ones; remote configuration overrides them.</summary>
public sealed record TriggerSettings
{
    public double WalkRadiusMeters { get; init; } = 100;
    public double BikeRadiusMeters { get; init; } = 250;
    public double CarMinRadiusMeters { get; init; } = 800;
    public double CarRadiusSpeedFactor { get; init; } = 1.2;
    public int WalkMinImportance { get; init; } = 50;
    public int BikeMinImportance { get; init; } = 60;
    public int CarMinImportance { get; init; } = 70;
    public double BikeConeDegrees { get; init; } = 90;
    public double CarConeDegrees { get; init; } = 60;
    public double FrontConeDegrees { get; init; } = 45;
    public double WalkMaxAccuracyMeters { get; init; } = 50;
    public double BikeMaxAccuracyMeters { get; init; } = 60;
    public double CarMaxAccuracyMeters { get; init; } = 100;
    public double BikeMinSpeedKmh { get; init; } = 7;
    public double CarMinSpeedKmh { get; init; } = 30;
    public int SpeedMedianWindow { get; init; } = 5;
    public double SpeedBaselineSeconds { get; init; } = 15;
    public double ModeSwitchSeconds { get; init; } = 30;
    public double CarAnticipationSeconds { get; init; } = 60;
    public double MinGapSeconds { get; init; } = 90;
    public int RepeatAfterDays { get; init; } = 30;
    public double StationaryStopMinutes { get; init; } = 5;
    public double StationaryRadiusMeters { get; init; } = 30;
    public double GpsLossSeconds { get; init; } = 20;
    public double MinTriggerScore { get; init; } = 0.45;
    public double ContextWeight { get; init; } = 0.10;

    /// <summary>Weight of the crowd penalty: 0 / 0.10 / 0.25 for the ethics setting off / balanced / strong (annexe E <c>reco.ethical</c>).</summary>
    public double CrowdWeight { get; init; } = 0.10;

    public double VisitRadiusMeters { get; init; } = 60;
    public double VisitMinMinutes { get; init; } = 5;
    public double VisitFullMinutes { get; init; } = 10;
    public double VisitMaxSpeedKmh { get; init; } = 2;
    public double AutoStopIdleHours { get; init; } = 2;

    /// <summary>
    /// A story published without audio (no TTS voice when it was produced) may be announced and read by the device's own voice (MVP-0: on).
    /// It only counts where the platform has a voice. Remote key <c>text_only_stories</c>.
    /// </summary>
    public bool AllowTextOnlyStories { get; init; } = true;

    public double MaxAccuracyFor(TravelMode mode) => mode switch { TravelMode.Walk => WalkMaxAccuracyMeters, TravelMode.Bike => BikeMaxAccuracyMeters, _ => CarMaxAccuracyMeters };

    public int MinImportanceFor(TravelMode mode) => mode switch { TravelMode.Walk => WalkMinImportance, TravelMode.Bike => BikeMinImportance, _ => CarMinImportance };

    public double RadiusFor(TravelMode mode, double speedMetersPerSecond) => mode switch
    {
        TravelMode.Walk => WalkRadiusMeters,
        TravelMode.Bike => BikeRadiusMeters,
        _ => Math.Max(CarMinRadiusMeters, speedMetersPerSecond * 60d * CarRadiusSpeedFactor),
    };

    /// <summary>Reads the remote <c>trigger</c> section; a key that is missing or malformed keeps its default.</summary>
    public static TriggerSettings FromJson(JsonElement section)
    {
        double Number(JsonElement parent, string name, double fallback) =>
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;

        JsonElement Child(string name) => section.ValueKind == JsonValueKind.Object && section.TryGetProperty(name, out var value) ? value : default;

        var defaults = new TriggerSettings();
        var radius = Child("radius_m");
        var importance = Child("min_importance");
        var cone = Child("heading_cone_deg");
        var accuracy = Child("max_accuracy_m");
        var speed = Child("speed_kmh");
        return new TriggerSettings
        {
            WalkRadiusMeters = Number(radius, "walk", defaults.WalkRadiusMeters),
            BikeRadiusMeters = Number(radius, "bike", defaults.BikeRadiusMeters),
            CarMinRadiusMeters = Number(radius, "car_min", defaults.CarMinRadiusMeters),
            CarRadiusSpeedFactor = Number(section, "car_radius_speed_factor", defaults.CarRadiusSpeedFactor),
            WalkMinImportance = (int)Number(importance, "walk", defaults.WalkMinImportance),
            BikeMinImportance = (int)Number(importance, "bike", defaults.BikeMinImportance),
            CarMinImportance = (int)Number(importance, "car", defaults.CarMinImportance),
            BikeConeDegrees = Number(cone, "bike", defaults.BikeConeDegrees),
            CarConeDegrees = Number(cone, "car", defaults.CarConeDegrees),
            FrontConeDegrees = Number(section, "front_cone_deg", defaults.FrontConeDegrees),
            WalkMaxAccuracyMeters = Number(accuracy, "walk", defaults.WalkMaxAccuracyMeters),
            BikeMaxAccuracyMeters = Number(accuracy, "bike", defaults.BikeMaxAccuracyMeters),
            CarMaxAccuracyMeters = Number(accuracy, "car", defaults.CarMaxAccuracyMeters),
            BikeMinSpeedKmh = Number(speed, "bike_min", defaults.BikeMinSpeedKmh),
            CarMinSpeedKmh = Number(speed, "car_min", defaults.CarMinSpeedKmh),
            SpeedMedianWindow = (int)Number(section, "speed_median_window", defaults.SpeedMedianWindow),
            ModeSwitchSeconds = Number(section, "mode_switch_seconds", defaults.ModeSwitchSeconds),
            CarAnticipationSeconds = Number(section, "car_anticipation_s", defaults.CarAnticipationSeconds),
            MinGapSeconds = Number(section, "min_gap_s", defaults.MinGapSeconds),
            RepeatAfterDays = (int)Number(section, "repeat_after_days", defaults.RepeatAfterDays),
            StationaryStopMinutes = Number(section, "stationary_stop_minutes", defaults.StationaryStopMinutes),
            GpsLossSeconds = Number(section, "gps_loss_seconds", defaults.GpsLossSeconds),
            MinTriggerScore = Number(section, "min_trigger_score", defaults.MinTriggerScore),
            VisitRadiusMeters = Number(section, "visit_radius_m", defaults.VisitRadiusMeters),
            VisitMinMinutes = Number(section, "visit_min_minutes", defaults.VisitMinMinutes),
            VisitFullMinutes = Number(section, "visit_full_minutes", defaults.VisitFullMinutes),
            VisitMaxSpeedKmh = Number(section, "visit_max_speed_kmh", defaults.VisitMaxSpeedKmh),
            AutoStopIdleHours = Number(section, "auto_stop_idle_hours", defaults.AutoStopIdleHours),
            AllowTextOnlyStories = section.ValueKind == JsonValueKind.Object && section.TryGetProperty("text_only_stories", out var textOnly) && textOnly.ValueKind is JsonValueKind.True or JsonValueKind.False ? textOnly.GetBoolean() : defaults.AllowTextOnlyStories,
        };
    }
}
