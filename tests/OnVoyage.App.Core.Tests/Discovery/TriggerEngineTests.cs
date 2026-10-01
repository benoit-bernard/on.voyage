using System.Text.Json;
using OnVoyage.App.Core.Discovery;

namespace OnVoyage.App.Core.Tests.Discovery;

public sealed class TriggerEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 14, 9, 0, 0, TimeSpan.Zero);
    private const double Lat = 43.2965;
    private const double Lon = 5.3700;

    private static TriggerCandidate Place(string name, double latitude, double longitude, int importance = 70, double baseScore = 0.7, int crowd = 2, bool fragile = false, bool road = false, bool car = false, bool story = true) =>
        new(Guid.NewGuid(), name, latitude, longitude, importance, baseScore, crowd, fragile, road, car, story ? Guid.NewGuid() : Guid.Empty);

    private static (double Lat, double Lon) North(double latitude, double longitude, double meters) => (latitude + (meters / 111_320d), longitude);

    private static (double Lat, double Lon) East(double latitude, double longitude, double meters) => (latitude, longitude + (meters / (111_320d * Math.Cos(latitude * Math.PI / 180d))));

    private static TriggerEngine Started(TriggerSettings? settings = null, ITriggerHistory? history = null, params TriggerCandidate[] candidates)
    {
        var engine = new TriggerEngine(settings ?? new TriggerSettings(), history ?? new InMemoryTriggerHistory());
        engine.SetCandidates(candidates);
        engine.Start();
        return engine;
    }

    private static LocationFix Fix(double seconds, double latitude = Lat, double longitude = Lon, double accuracy = 10, double? speed = null, double? heading = null) =>
        new(latitude, longitude, accuracy, speed, heading, T0.AddSeconds(seconds));

    /// <summary>Moves east at a steady speed with the device's own speed and heading, one fix per second, and returns the last outcome.</summary>
    private static TriggerOutcome Drive(TriggerEngine engine, double metersPerSecond, int seconds, double heading = 90, double fromSecond = 0, double startLat = Lat, double startLon = Lon)
    {
        TriggerOutcome? last = null;
        for (var s = 0; s <= seconds; s++)
        {
            var (latitude, longitude) = East(startLat, startLon, metersPerSecond * s);
            last = engine.OnFix(Fix(fromSecond + s, latitude, longitude, 8, metersPerSecond, heading));
            if (last.Trigger is not null)
            {
                return last;
            }
        }

        return last!;
    }

    /// <summary>Four fixes in the same spot at walking speed: enough for the engine to know the mode, then the outcome of the one that decided.</summary>
    private static TriggerOutcome Hear(TriggerEngine engine, double latitude = Lat, double longitude = Lon, double fromSecond = 0, double accuracy = 8, double heading = 90)
    {
        TriggerOutcome? last = null;
        for (var s = 0; s < 4; s++)
        {
            last = engine.OnFix(Fix(fromSecond + s, latitude, longitude, accuracy, 1.2, heading));
            if (last.Trigger is not null)
            {
                return last;
            }
        }

        return last!;
    }

    [Fact]
    public void The_shipped_defaults_are_the_values_of_the_remote_trigger_section_of_annexe_e()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "default-config.json")));

        var fromConfig = TriggerSettings.FromJson(document.RootElement.GetProperty("trigger"));

        fromConfig.ShouldBe(new TriggerSettings());
    }

    [Fact]
    public void A_missing_or_malformed_key_keeps_its_default()
    {
        using var document = JsonDocument.Parse("""{ "radius_m": { "walk": 150, "bike": "x" }, "min_gap_s": null, "min_trigger_score": 0.6 }""");

        var settings = TriggerSettings.FromJson(document.RootElement);

        settings.WalkRadiusMeters.ShouldBe(150);
        settings.BikeRadiusMeters.ShouldBe(250);
        settings.MinGapSeconds.ShouldBe(90);
        settings.MinTriggerScore.ShouldBe(0.6);
    }

    [Fact]
    public void Nothing_happens_while_the_engine_is_off()
    {
        var engine = new TriggerEngine(new TriggerSettings(), new InMemoryTriggerHistory());
        engine.SetCandidates([Place("Ici", Lat, Lon)]);

        engine.OnFix(Fix(0)).Reason.ShouldBe(SkipReason.Off);
        engine.State.ShouldBe(EngineState.Off);
    }

    [Fact]
    public void A_walker_next_to_a_place_hears_it_and_the_trigger_carries_no_coordinates()
    {
        var place = Place("Fort", Lat, Lon);
        var engine = Started(candidates: place);

        engine.OnFix(Fix(0, speed: 1.2)).Reason.ShouldBe(SkipReason.WarmingUp);
        engine.OnFix(Fix(1, speed: 1.2)).Reason.ShouldBe(SkipReason.WarmingUp);
        var outcome = engine.OnFix(Fix(2, speed: 1.2));

        outcome.Trigger.ShouldNotBeNull().PoiId.ShouldBe(place.PoiId);
        outcome.Trigger.StoryId.ShouldBe(place.StoryId);
        outcome.Trigger.DistanceMeters.ShouldBe(0);
        outcome.State.ShouldBe(EngineState.Announcing);
        typeof(Trigger).GetProperties().Select(property => property.Name).ShouldNotContain(name => name.Contains("Lat", StringComparison.OrdinalIgnoreCase) || name.Contains("Lon", StringComparison.OrdinalIgnoreCase));
        typeof(Visit).GetProperties().Select(property => property.Name).ShouldNotContain(name => name.Contains("Lat", StringComparison.OrdinalIgnoreCase) || name.Contains("Lon", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(80, true)]
    [InlineData(120, false)]
    public void The_walking_radius_is_100_metres_and_the_distance_is_rounded_to_50(double metres, bool triggered)
    {
        var (latitude, longitude) = North(Lat, Lon, metres);
        var engine = Started(candidates: Place("Fort", latitude, longitude));

        var outcome = Hear(engine);

        (outcome.Trigger is not null).ShouldBe(triggered);
        if (triggered)
        {
            outcome.Trigger!.DistanceMeters.ShouldBe(metres < 75 ? 50 : 100);
        }
    }

    [Fact]
    public void A_fix_less_precise_than_the_threshold_is_ignored_and_a_driver_tolerates_more_than_a_walker()
    {
        var walker = Started(candidates: Place("Fort", Lat, Lon));
        walker.OnFix(Fix(0, accuracy: 60)).Reason.ShouldBe(SkipReason.InaccurateFix);
        Hear(walker, fromSecond: 1, accuracy: 49).Trigger.ShouldNotBeNull();

        var driver = Started();
        Drive(driver, 20, 8, heading: 90);
        driver.Mode.ShouldBe(TravelMode.Car);
        driver.OnFix(Fix(9, accuracy: 90)).Reason.ShouldNotBe(SkipReason.InaccurateFix);
        driver.OnFix(Fix(10, accuracy: 120)).Reason.ShouldBe(SkipReason.InaccurateFix);
    }

    [Fact]
    public void An_unreadable_or_out_of_order_fix_is_ignored()
    {
        var engine = Started(candidates: Place("Loin", Lat + 0.5, Lon));
        engine.OnFix(Fix(10));

        engine.OnFix(Fix(5)).Reason.ShouldBe(SkipReason.OutOfOrder);
        engine.OnFix(Fix(10)).Reason.ShouldBe(SkipReason.OutOfOrder);
        engine.OnFix(Fix(11, accuracy: double.NaN)).Reason.ShouldBe(SkipReason.InaccurateFix);
    }

    [Fact]
    public void The_mode_follows_the_smoothed_speed_and_changes_only_after_30_steady_seconds()
    {
        var engine = Started();
        Drive(engine, 1.2, 20);
        engine.Mode.ShouldBe(TravelMode.Walk);

        // 6 m/s ≈ 21 km/h: the first estimates are below, then the median passes 7 km/h; the switch waits 30 s.
        var origin = East(Lat, Lon, 24);
        for (var s = 1; s <= 80; s++)
        {
            var (latitude, longitude) = East(origin.Lat, origin.Lon, 6 * s);
            engine.OnFix(Fix(20 + s, latitude, longitude, 8, 6, 90));
            if (s < 25)
            {
                engine.Mode.ShouldBe(TravelMode.Walk, $"still in hysteresis at +{s} s");
            }
        }

        engine.Mode.ShouldBe(TravelMode.Bike);
    }

    [Fact]
    public void A_short_burst_of_speed_does_not_change_the_mode()
    {
        var engine = Started();
        Drive(engine, 1.2, 30);
        var origin = East(Lat, Lon, 36);

        for (var s = 1; s <= 8; s++)
        {
            var (latitude, longitude) = East(origin.Lat, origin.Lon, 9 * s);
            engine.OnFix(Fix(30 + s, latitude, longitude, 8, 9, 90));
        }

        for (var s = 9; s <= 40; s++)
        {
            var (latitude, longitude) = East(origin.Lat, origin.Lon, 72 + (1.2 * (s - 8)));
            engine.OnFix(Fix(30 + s, latitude, longitude, 8, 1.2, 90));
        }

        engine.Mode.ShouldBe(TravelMode.Walk);
    }

    [Fact]
    public void Without_a_device_speed_it_is_measured_over_several_seconds_so_gps_noise_does_not_make_a_walker_a_cyclist()
    {
        var engine = Started();
        var random = new Random(1);
        for (var s = 0; s < 120; s++)
        {
            var (latitude, longitude) = East(Lat, Lon, 0.9 * s);
            var noisy = North(latitude, longitude, (random.NextDouble() - 0.5) * 10);
            engine.OnFix(Fix(s, noisy.Lat, noisy.Lon));
        }

        engine.Mode.ShouldBe(TravelMode.Walk);
        engine.SmoothedSpeedMetersPerSecond.ShouldBeInRange(0.5, 1.6);
    }

    [Theory]
    [InlineData(2, 100, false)] // a major landmark 40 m ahead, already told 2 days ago
    [InlineData(35, 100, true)]
    public void A_place_told_less_than_30_days_ago_waits(int daysAgo, int _, bool triggered)
    {
        var place = Place("Fort", Lat, Lon);
        var history = new InMemoryTriggerHistory();
        history.MarkTold(place.PoiId, T0.AddDays(-daysAgo));
        var engine = Started(history: history, candidates: place);

        (Hear(engine).Trigger is not null).ShouldBe(triggered);
    }

    [Fact]
    public void A_place_just_told_in_this_session_is_not_told_again_even_without_history()
    {
        var place = Place("Fort", Lat, Lon);
        var engine = Started(candidates: place);
        Hear(engine).Trigger.ShouldNotBeNull();
        engine.OnStoryStarted();
        engine.OnPlaybackEnded(T0.AddSeconds(100));

        Hear(engine, fromSecond: 500).Trigger.ShouldBeNull();
    }

    [Theory]
    [InlineData("fragile")]
    [InlineData("excluded")]
    [InlineData("no story")]
    [InlineData("low importance")]
    [InlineData("low score")]
    public void Some_places_are_never_announced(string reason)
    {
        var place = reason switch
        {
            "fragile" => Place("P", Lat, Lon, fragile: true),
            "no story" => Place("P", Lat, Lon, story: false),
            "low importance" => Place("P", Lat, Lon, importance: 49),
            "low score" => Place("P", Lat, Lon, baseScore: 0.44),
            _ => Place("P", Lat, Lon),
        };
        var engine = Started(candidates: place);
        if (reason == "excluded")
        {
            engine.SetExcluded([place.PoiId]);
        }

        Hear(engine).Trigger.ShouldBeNull();
    }

    [Fact]
    public void The_crowd_penalty_follows_the_ethics_setting()
    {
        var crowded = Place("Plage", Lat, Lon, baseScore: 0.5, crowd: 5);

        // balanced: 0.5 - 0.10 = 0.40 < 0.45 ; off: 0.5 ; strong would be lower still
        Hear(Started(candidates: crowded)).Trigger.ShouldBeNull();
        Hear(Started(new TriggerSettings { CrowdWeight = 0 }, candidates: crowded)).Trigger.ShouldNotBeNull();
    }

    [Fact]
    public void The_best_place_maximises_score_times_closeness()
    {
        var near = Place("Proche", North(Lat, Lon, 10).Lat, Lon, baseScore: 0.5);
        var far = Place("Loin", North(Lat, Lon, 90).Lat, Lon, baseScore: 0.9);
        var strong = Place("Fort et proche", North(Lat, Lon, 30).Lat, Lon, baseScore: 0.9);

        Hear(Started(candidates: [far, near])).Trigger!.PoiId.ShouldBe(near.PoiId);
        Hear(Started(candidates: [far, near, strong])).Trigger!.PoiId.ShouldBe(strong.PoiId);
    }

    [Fact]
    public void A_triggered_story_blocks_others_until_it_ends_plus_the_minimum_gap()
    {
        var first = Place("Premier", Lat, Lon);
        var second = Place("Second", North(Lat, Lon, 20).Lat, Lon);
        var engine = Started(candidates: [first, second]);

        Hear(engine).Trigger.ShouldNotBeNull();
        engine.OnFix(Fix(5, speed: 1.2)).Reason.ShouldBe(SkipReason.Busy);
        engine.OnStoryStarted();
        engine.State.ShouldBe(EngineState.Playing);
        engine.OnFix(Fix(60, speed: 1.2)).Reason.ShouldBe(SkipReason.Busy);

        engine.OnPlaybackEnded(T0.AddSeconds(100));
        engine.State.ShouldBe(EngineState.Cooldown);
        engine.OnFix(Fix(150, speed: 1.2)).Reason.ShouldBe(SkipReason.Busy);
        engine.OnFix(Fix(189, speed: 1.2)).Reason.ShouldBe(SkipReason.Busy);
        engine.OnFix(Fix(191, speed: 1.2)).Trigger.ShouldNotBeNull().PoiId.ShouldBe(second.PoiId);
    }

    [Fact]
    public void Nothing_is_triggered_during_a_phone_call()
    {
        var engine = Started(candidates: Place("Fort", Lat, Lon));
        engine.OnCallStateChanged(true);
        Hear(engine).Reason.ShouldBe(SkipReason.InCall);

        engine.OnCallStateChanged(false);
        Hear(engine, fromSecond: 5).Trigger.ShouldNotBeNull();
    }

    [Fact]
    public void A_traveler_standing_still_for_five_minutes_triggers_nothing_new()
    {
        var engine = Started();
        for (var s = 0; s <= 360; s += 5)
        {
            engine.OnFix(Fix(s, speed: 0));
        }

        engine.SetCandidates([Place("Fort", Lat, Lon)]);
        engine.OnFix(Fix(365, speed: 0)).Reason.ShouldBe(SkipReason.Immobile);

        // Walking away re-arms the engine.
        var away = East(Lat, Lon, 60);
        engine.OnFix(Fix(370, away.Lat, away.Lon, speed: 0)).Reason.ShouldNotBe(SkipReason.Immobile);
    }

    [Fact]
    public void A_signal_lost_for_over_20_seconds_is_reported_and_clears_with_the_next_position()
    {
        var engine = Started();
        engine.OnFix(Fix(0));

        engine.OnTick(T0.AddSeconds(15));
        engine.SignalLost.ShouldBeFalse();
        engine.OnTick(T0.AddSeconds(25));
        engine.SignalLost.ShouldBeTrue();

        engine.OnFix(Fix(60));
        engine.SignalLost.ShouldBeFalse();
    }

    [Fact]
    public void After_a_gap_the_speed_history_is_dropped_so_a_jump_is_not_taken_for_speed()
    {
        var engine = Started();
        Drive(engine, 1.2, 30);
        var far = East(Lat, Lon, 600);

        engine.OnFix(Fix(150, far.Lat, far.Lon));

        engine.Mode.ShouldBe(TravelMode.Walk);
        engine.SmoothedSpeedMetersPerSecond.ShouldBe(0, "no estimate yet: nothing is extrapolated across the gap");
    }

    [Theory]
    [InlineData(0, 45, AnnouncementDirection.Front)]
    [InlineData(90, 0, AnnouncementDirection.Left)] // heading east, place to the north
    [InlineData(90, 180, AnnouncementDirection.Right)] // heading east, place to the south
    [InlineData(90, 90, AnnouncementDirection.Front)]
    [InlineData(270, 0, AnnouncementDirection.Right)] // heading west, place to the north
    public void The_announcement_says_ahead_left_or_right_from_the_heading(double heading, double placeBearing, AnnouncementDirection expected)
    {
        var rad = placeBearing * Math.PI / 180d;
        var latitude = Lat + (Math.Cos(rad) * 40 / 111_320d);
        var longitude = Lon + (Math.Sin(rad) * 40 / (111_320d * Math.Cos(Lat * Math.PI / 180d)));
        var engine = Started(candidates: Place("Fort", latitude, longitude));

        var outcome = Hear(engine, heading: heading);

        outcome.Trigger.ShouldNotBeNull().Direction.ShouldBe(expected);
    }

    [Fact]
    public void In_a_car_the_radius_grows_with_the_speed_and_the_announcement_comes_about_a_minute_ahead()
    {
        // 25 m/s = 90 km/h: radius = max(800, 25 x 60 x 1.2 = 1800); reach = 25 x 60 = 1500 m (an arrival in a minute).
        var ahead = East(Lat, Lon, 2500);
        var engine = Started(candidates: Place("Belvédère", ahead.Lat, ahead.Lon, road: true));

        Drive(engine, 25, 30).Trigger.ShouldBeNull();

        var next = East(Lat, Lon, 25 * 41);
        var outcome = engine.OnFix(Fix(41, next.Lat, next.Lon, 8, 25, 90));
        outcome.Trigger.ShouldNotBeNull().Anticipated.ShouldBeTrue();
        outcome.Trigger.DistanceMeters.ShouldBeInRange(1450, 1500);
        engine.Mode.ShouldBe(TravelMode.Car);
    }

    [Fact]
    public void In_a_car_a_place_behind_is_ignored_and_without_a_heading_the_engine_does_not_guess()
    {
        var behind = East(Lat, Lon, -300);
        var engine = Started(candidates: Place("Derrière", behind.Lat, behind.Lon, road: true));

        Drive(engine, 15, 60, fromSecond: 0).Trigger.ShouldBeNull();

        var unknownHeading = Started(candidates: Place("Devant", East(Lat, Lon, 200).Lat, East(Lat, Lon, 200).Lon, road: true));
        for (var s = 0; s < 40; s++)
        {
            // Same spot twice per fix would give no displacement: positions barely move, no device heading, so no heading is ever known.
            unknownHeading.OnFix(Fix(s, speed: 15)).Trigger.ShouldBeNull();
        }
    }

    [Fact]
    public void In_a_car_only_important_or_roadside_places_qualify()
    {
        var ahead = East(Lat, Lon, 600);
        var minor = Place("Mineur", ahead.Lat, ahead.Lon, importance: 55);
        var roadside = Place("Au bord de la route", ahead.Lat, ahead.Lon, importance: 55, road: true);
        var accessible = Place("Accessible", ahead.Lat, ahead.Lon, importance: 55, car: true);

        Drive(Started(candidates: minor), 15, 30).Trigger.ShouldBeNull();
        Drive(Started(candidates: roadside), 15, 30).Trigger.ShouldNotBeNull();
        Drive(Started(candidates: accessible), 15, 30).Trigger.ShouldNotBeNull();
    }

    [Fact]
    public void A_cyclist_hears_places_within_250_metres_in_a_cone_of_90_degrees()
    {
        var aheadLeft = North(East(Lat, Lon, 300).Lat, East(Lat, Lon, 300).Lon, 120);
        var behind = East(Lat, Lon, -100);
        var place = Place("Devant", aheadLeft.Lat, aheadLeft.Lon, importance: 65);
        var behindPlace = Place("Derrière", behind.Lat, behind.Lon, importance: 65);

        var engine = Started(candidates: [behindPlace, place]);
        var told = Drive(engine, 6, 60);

        told.Trigger.ShouldNotBeNull().PoiId.ShouldBe(place.PoiId);
        told.Mode.ShouldBe(TravelMode.Bike);
    }

    [Fact]
    public void A_stay_of_twelve_minutes_at_a_place_is_a_visit_with_full_confidence_and_no_coordinates()
    {
        var place = Place("Fort", Lat, Lon);
        var history = new InMemoryTriggerHistory();
        history.MarkTold(place.PoiId, T0.AddDays(-1)); // told yesterday: no new story, but the stay still counts
        var engine = Started(history: history, candidates: place);
        for (var s = 0; s <= 12 * 60; s += 10)
        {
            engine.OnFix(Fix(s, accuracy: 10, speed: 0));
        }

        var visits = engine.Stop();

        var visit = visits.ShouldHaveSingleItem();
        visit.PoiId.ShouldBe(place.PoiId);
        visit.Dwell.TotalMinutes.ShouldBe(12, 0.5);
        visit.Confidence.ShouldBe(1.0, 0.001);
    }

    [Theory]
    [InlineData(6, 10, 0.6)]
    [InlineData(6, 40, 0.48)]
    [InlineData(6, 70, 0.3)]
    [InlineData(20, 10, 1.0)]
    public void The_confidence_of_a_visit_grows_with_the_stay_and_falls_with_a_poor_precision(int minutes, double accuracy, double expected)
    {
        var place = Place("Fort", Lat, Lon);
        var history = new InMemoryTriggerHistory();
        history.MarkTold(place.PoiId, T0.AddDays(-1));
        var settings = new TriggerSettings { WalkMaxAccuracyMeters = 100 };
        var engine = Started(settings, history, place);
        for (var s = 0; s <= minutes * 60; s += 10)
        {
            engine.OnFix(Fix(s, accuracy: accuracy, speed: 0));
        }

        engine.Stop().ShouldHaveSingleItem().Confidence.ShouldBe(expected, 0.02);
    }

    [Fact]
    public void A_stay_shorter_than_five_minutes_or_while_moving_is_not_a_visit()
    {
        var place = Place("Fort", Lat, Lon);
        var history = new InMemoryTriggerHistory();
        history.MarkTold(place.PoiId, T0.AddDays(-1));

        var brief = Started(history: history, candidates: place);
        for (var s = 0; s <= 240; s += 10)
        {
            brief.OnFix(Fix(s, speed: 0));
        }

        brief.Stop().ShouldBeEmpty();

        var passing = Started(history: history, candidates: place);
        for (var s = 0; s <= 600; s += 10)
        {
            passing.OnFix(Fix(s, speed: 1.2));
        }

        passing.Stop().ShouldBeEmpty("walking at 4 km/h is above the 2 km/h visit limit");
    }

    [Fact]
    public void A_visit_ends_when_the_traveler_leaves()
    {
        var place = Place("Fort", Lat, Lon);
        var history = new InMemoryTriggerHistory();
        history.MarkTold(place.PoiId, T0.AddDays(-1));
        var engine = Started(history: history, candidates: place);
        for (var s = 0; s <= 400; s += 10)
        {
            engine.OnFix(Fix(s, speed: 0));
        }

        var away = North(Lat, Lon, 300);
        var outcome = engine.OnFix(Fix(410, away.Lat, away.Lon, speed: 0));

        outcome.Visits.ShouldHaveSingleItem().Dwell.TotalMinutes.ShouldBeGreaterThan(5);
        engine.Stop().ShouldBeEmpty();
    }

    [Fact]
    public void Discovery_switches_itself_off_after_two_hours_without_moving()
    {
        var engine = Started();
        engine.OnFix(Fix(0));

        engine.IdleTooLong(T0.AddMinutes(100)).ShouldBeFalse();
        engine.IdleTooLong(T0.AddMinutes(121)).ShouldBeTrue();
    }

    [Fact]
    public void Haversine_and_bearing_agree_with_known_values()
    {
        GeoMath.DistanceMeters(43.2965, 5.37, 43.2965, 5.37).ShouldBe(0);
        GeoMath.DistanceMeters(0, 0, 0, 1).ShouldBe(111_195, 100);
        GeoMath.BearingDegrees(0, 0, 1, 0).ShouldBe(0, 0.001);
        GeoMath.BearingDegrees(0, 0, 0, 1).ShouldBe(90, 0.001);
        GeoMath.SignedAngleDegrees(350, 10).ShouldBe(20);
        GeoMath.SignedAngleDegrees(10, 350).ShouldBe(-20);
        GeoMath.Median([3, 1, 2]).ShouldBe(2);
        GeoMath.Median([1, 2, 3, 4]).ShouldBe(2.5);
    }
}
