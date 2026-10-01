using OnVoyage.App.Core.Discovery;

namespace OnVoyage.App.Core.Tests.Discovery;

/// <summary>Replays of the synthetic traces of <c>data-pipeline/gpx</c> (§20.4). The numbers asked for by the acceptance criteria of F-09 and F-10 are asserted; the full list is approved.</summary>
public sealed class TriggerReplayTests
{
    [Fact]
    public void Walking_the_old_port_tells_between_three_and_six_stories_never_closer_than_the_minimum_gap()
    {
        var result = Replay.Run("walk_vieux_port");

        result.Triggers.Count.ShouldBeInRange(3, 6);
        result.Triggers.ShouldAllBe(trigger => trigger.Mode == TravelMode.Walk);
        for (var i = 1; i < result.Triggers.Count; i++)
        {
            (result.Triggers[i].AtSeconds - result.Triggers[i - 1].EndsAtSeconds).ShouldBeGreaterThanOrEqualTo(90, "90 s between the end of a story and the start of the next");
        }

        result.Triggers.Select(trigger => trigger.Place).ShouldNotContain("Petit oratoire sans importance", "importance 20 is below the walking threshold of 50");
        result.Triggers.Select(trigger => trigger.Place).Distinct().Count().ShouldBe(result.Triggers.Count, "a place is told once");
        result.Triggers.ShouldAllBe(trigger => trigger.DistanceMeters <= 100);
        Approvals.Verify("walk_vieux_port", result.Triggers);
    }

    [Fact]
    public void A_place_told_three_days_ago_is_not_told_again_but_one_told_forty_days_ago_is()
    {
        var places = GpxReader.ReadPlaces();
        var start = GpxReader.Read("walk_vieux_port")[0].Timestamp;
        var recent = new InMemoryTriggerHistory();
        var old = new InMemoryTriggerHistory();
        foreach (var place in places)
        {
            recent.MarkTold(place.PoiId, start.AddDays(-3));
            old.MarkTold(place.PoiId, start.AddDays(-40));
        }

        Replay.Run("walk_vieux_port", history: recent).Triggers.ShouldBeEmpty();
        Replay.Run("walk_vieux_port", history: old).Triggers.Count.ShouldBeInRange(3, 6);
    }

    [Fact]
    public void A_two_minute_gps_loss_triggers_nothing_on_an_extrapolated_position()
    {
        var result = Replay.Run("tunnel_loss");

        var gap = result.Gaps.ShouldHaveSingleItem();
        (gap.To - gap.From).ShouldBeGreaterThan(110);
        result.SignalWasLost.ShouldBeTrue("the engine noticed the loss");
        result.Triggers.ShouldAllBe(trigger => trigger.AtSeconds <= gap.From || trigger.AtSeconds >= gap.To);
        result.Triggers.Select(trigger => trigger.Place).ShouldNotContain("Place dans le tunnel (test)", "the place lies in the middle of the 660 m gap, more than the 250 m radius from the last and from the first position");
        Approvals.Verify("tunnel_loss", result.Triggers);
    }

    [Fact]
    public void An_urban_canyon_with_poor_positions_never_triggers_on_a_poor_position()
    {
        var result = Replay.Run("gps_jitter_urban_canyon");

        result.RejectedFixes.ShouldBeGreaterThan(20, "most fixes of the trace are worse than the walking threshold of 50 m");
        result.Triggers.ShouldAllBe(trigger => trigger.FixAccuracy <= 50);
        result.Triggers.Count.ShouldBeLessThanOrEqualTo(3);
        Approvals.Verify("gps_jitter_urban_canyon", result.Triggers);
    }

    [Fact]
    public void Cycling_the_corniche_only_tells_places_ahead_within_the_cone()
    {
        var result = Replay.Run("bike_corniche");

        result.Triggers.ShouldNotBeEmpty();
        result.Triggers.Where(trigger => trigger.AtSeconds > 60).ShouldAllBe(trigger => trigger.Mode == TravelMode.Bike);
        result.Triggers.ShouldAllBe(trigger => trigger.DistanceMeters <= 250);
        Approvals.Verify("bike_corniche", result.Triggers);
    }

    [Fact]
    public void Driving_the_ridge_road_tells_nothing_about_a_place_behind_and_nothing_fragile()
    {
        var result = Replay.Run("car_route_des_cretes");

        result.Triggers.Select(trigger => trigger.Place).ShouldContain("Belvédère des Crêtes (test)");
        result.Triggers.Select(trigger => trigger.Place).ShouldNotContain("Point de vue derrière (test)", "it lies behind the vehicle, outside the ±60° cone");
        result.Triggers.Select(trigger => trigger.Place).ShouldNotContain("Calanque fragile (test)", "fragile places are never announced");
        result.Triggers.ShouldAllBe(trigger => trigger.Mode == TravelMode.Car && trigger.Anticipated);
        Approvals.Verify("car_route_des_cretes", result.Triggers);
    }

    [Fact]
    public void On_a_motorway_at_110_the_radius_grows_with_the_speed_and_only_important_or_visible_places_qualify()
    {
        var result = Replay.Run("car_a50_highway_110kmh");

        var told = result.Triggers.Select(trigger => trigger.Place).ToList();
        told.ShouldContain("Repère autoroute important (test)");
        told.ShouldNotContain("Repère autoroute mineur (test)", "importance 40 and not visible from the road");
        // The announcement comes about a minute before reaching the place, which at 110 km/h is far outside the 800 m minimum radius.
        result.Triggers.First(trigger => trigger.Place == "Repère autoroute important (test)").DistanceMeters.ShouldBeGreaterThan(800);
        Approvals.Verify("car_a50_highway_110kmh", result.Triggers);
    }

    [Fact]
    public void Replaying_the_same_trace_twice_gives_the_same_result()
    {
        Replay.Run("walk_vieux_port").Triggers.ShouldBe(Replay.Run("walk_vieux_port").Triggers);
    }
}
