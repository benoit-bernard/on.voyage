using OnVoyage.Recommendation.Engine.Planning;

namespace OnVoyage.Recommendation.Engine.Tests;

public sealed class VisitPlannerTests
{
    private static readonly (double, double) Center = (43.2965, 5.3698);
    private static readonly string[] Categories = ["history", "nature", "culture", "architecture", "religion", "gastronomy", "leisure"];

    private static List<PlannerPlace> City(int seed, int count = 60, double spreadKm = 6)
    {
        var random = new Random(seed);
        return [.. Enumerable.Range(0, count).Select(i => new PlannerPlace(
            $"p{i:000}",
            Center.Item1 + ((random.NextDouble() - 0.5) * spreadKm / 111d),
            Center.Item2 + ((random.NextDouble() - 0.5) * spreadKm / 81d),
            random.NextDouble(),
            Categories[random.Next(Categories.Length)]))];
    }

    private static double Diameter(IReadOnlyList<PlannerPlace> day) =>
        day.SelectMany(a => day.Select(b => VisitPlanner.Distance(a.Latitude, a.Longitude, b.Latitude, b.Longitude))).DefaultIfEmpty(0).Max();

    [Fact]
    public void Two_days_on_foot_give_two_groups_of_four_to_six_places_within_three_kilometres()
    {
        var plan = VisitPlanner.Plan(City(1), 2, TravelMode.Walk, Center, Guid.NewGuid());

        plan.Count.ShouldBe(2);
        plan.ShouldAllBe(day => day.Places.Count >= 4 && day.Places.Count <= 6);
        plan.ShouldAllBe(day => Diameter(day.Places) <= 3_000d);
    }

    [Fact]
    public void Days_are_numbered_by_decreasing_mean_score_and_no_place_repeats()
    {
        var plan = VisitPlanner.Plan(City(2), 3, TravelMode.Bike, Center, Guid.NewGuid());

        plan.Select(d => d.MeanScore).ShouldBe(plan.Select(d => d.MeanScore).OrderDescending());
        plan.Select(d => d.Day).ShouldBe(Enumerable.Range(1, plan.Count));
        var all = plan.SelectMany(d => d.Places).Select(p => p.Id).ToList();
        all.Distinct().Count().ShouldBe(all.Count);
    }

    [Fact]
    public void Each_day_starts_at_the_place_closest_to_the_centre()
    {
        var plan = VisitPlanner.Plan(City(3), 2, TravelMode.Car, Center, Guid.NewGuid());
        foreach (var day in plan)
        {
            var closest = day.Places.MinBy(p => VisitPlanner.Distance(p.Latitude, p.Longitude, Center.Item1, Center.Item2))!;
            day.Places[0].ShouldBe(closest);
        }
    }

    [Fact]
    public void The_same_seed_gives_the_same_plan_and_the_input_order_does_not_matter()
    {
        var seed = Guid.NewGuid();
        var places = City(4);
        var first = VisitPlanner.Plan(places, 2, TravelMode.Walk, Center, seed);
        var second = VisitPlanner.Plan([.. places.AsEnumerable().Reverse()], 2, TravelMode.Walk, Center, seed);
        second.Select(d => d.Places.Select(p => p.Id)).ShouldBe(first.Select(d => d.Places.Select(p => p.Id)));
    }

    [Fact]
    public void No_category_exceeds_the_ceiling_of_the_candidate_list()
    {
        var places = City(5, 80).Select(p => p with { Category = p.Score > 0.3 ? "history" : p.Category }).ToList();
        var candidates = VisitPlanner.Diversify([.. places.OrderByDescending(p => p.Score)], 12, new PlannerOptions());
        candidates.Count.ShouldBe(12);
        candidates.GroupBy(p => p.Category).Max(g => g.Count()).ShouldBeLessThanOrEqualTo((int)Math.Ceiling(0.4 * 12));
    }

    [Fact]
    public void A_place_too_far_from_the_others_is_dropped_for_the_next_candidate()
    {
        var places = City(6, 40, 2);
        places.Add(new PlannerPlace("far", Center.Item1 + 0.4, Center.Item2, 1.0, "nature")); // ~44 km away, best score
        var plan = VisitPlanner.Plan(places, 1, TravelMode.Walk, Center, Guid.NewGuid());
        plan.SelectMany(d => d.Places).ShouldNotContain(p => p.Id == "far");
        plan.Single().Places.Count.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public void A_short_catalogue_returns_what_exists_and_an_empty_one_nothing()
    {
        VisitPlanner.Plan([], 2, TravelMode.Walk, Center, Guid.NewGuid()).ShouldBeEmpty();
        var plan = VisitPlanner.Plan(City(7, 3, 1), 4, TravelMode.Walk, Center, Guid.NewGuid());
        plan.SelectMany(d => d.Places).Count().ShouldBe(3);
    }

    [Fact]
    public void Days_outside_one_to_four_are_clamped()
    {
        VisitPlanner.Plan(City(8), 9, TravelMode.Car, Center, Guid.NewGuid()).Count.ShouldBeLessThanOrEqualTo(4);
        VisitPlanner.Plan(City(8), 0, TravelMode.Car, Center, Guid.NewGuid()).Count.ShouldBe(1);
    }

    [Fact]
    public void Property_the_order_is_shorter_than_a_random_order_in_at_least_95_percent_of_draws()
    {
        var wins = 0;
        var trials = 0;
        for (var city = 0; city < 20; city++)
        {
            var plan = VisitPlanner.Plan(City(100 + city), 2, TravelMode.Walk, Center, Guid.NewGuid());
            var random = new Random(city);
            foreach (var day in plan)
            {
                var length = VisitPlanner.PathLength(day.Places);
                for (var draw = 0; draw < 20; draw++)
                {
                    trials++;
                    if (length < VisitPlanner.PathLength([.. day.Places.OrderBy(_ => random.Next())]))
                    {
                        wins++;
                    }
                }
            }
        }

        ((double)wins / trials).ShouldBeGreaterThanOrEqualTo(0.95);
    }
}
