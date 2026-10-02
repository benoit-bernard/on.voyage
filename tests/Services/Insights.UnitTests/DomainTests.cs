using OnVoyage.Insights.Domain;

namespace Insights.UnitTests;

public sealed class DomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_partition_is_named_after_its_month_and_the_name_can_be_read_back()
    {
        var month = PartitionMonth.Of(Now);

        month.TableName.ShouldBe("event_y2026m10");
        month.Start.ShouldBe(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        month.End.ShouldBe(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        PartitionMonth.TryParse("event_y2026m10", out var parsed).ShouldBeTrue();
        parsed.ShouldBe(month);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("event_y2026m13")]
    [InlineData("event_y2026m00")]
    [InlineData("event_y26m10")]
    [InlineData("event_default")]
    [InlineData("event_y2026m10; drop table x")]
    public void Names_that_are_not_partitions_are_not_parsed(string name) => PartitionMonth.TryParse(name, out _).ShouldBeFalse();

    [Fact]
    public void The_required_partitions_cover_the_previous_month_and_the_next_two()
    {
        var required = EventPartitions.Required(Now);

        required.Select(m => m.TableName).ShouldBe(["event_y2026m09", "event_y2026m10", "event_y2026m11", "event_y2026m12"]);
        EventPartitions.Required(new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero)).Select(m => m.TableName)
            .ShouldBe(["event_y2026m11", "event_y2026m12", "event_y2027m01", "event_y2027m02"]);
    }

    [Fact]
    public void Only_months_entirely_before_the_cutoff_are_expired()
    {
        // 13 months before 2026-10-15 is 2025-09-15: September 2025 still holds events worth keeping, August 2025 does not.
        var cutoff = EventPartitions.RawCutoff(Now, 13);
        var existing = new[] { new PartitionMonth(2025, 7), new PartitionMonth(2025, 8), new PartitionMonth(2025, 9), new PartitionMonth(2026, 10) };

        EventPartitions.Expired(existing, cutoff).ShouldBe([new PartitionMonth(2025, 7), new PartitionMonth(2025, 8)]);
    }

    private static KpiTotal T(string cohort, string metric, double value) => new(cohort, metric, value);

    [Fact]
    public void The_central_hypothesis_is_the_ratio_of_the_click_rates_of_the_two_cohorts()
    {
        var values = KpiCalculator.Compute(
        [
            T("personalized", KpiMetrics.RecommendationViewed, 1200), T("personalized", KpiMetrics.RecommendationClicked, 180),
            T("control", KpiMetrics.RecommendationViewed, 1000), T("control", KpiMetrics.RecommendationClicked, 100),
        ], null);

        values["central_ctr_ratio"].Value!.Value.ShouldBe(1.5, 1e-9);
        values["central_ctr_ratio"].Sample.ShouldBe(1000); // the smaller arm: the target asks for 1 000 impressions per cohort
    }

    [Fact]
    public void The_ratio_needs_both_arms_and_is_not_computed_for_a_single_cohort()
    {
        KpiTotal[] onlyPersonalized = [T("personalized", KpiMetrics.RecommendationViewed, 100), T("personalized", KpiMetrics.RecommendationClicked, 10)];
        KpiCalculator.Compute(onlyPersonalized, null).ShouldNotContainKey("central_ctr_ratio");

        KpiTotal[] both = [T("control", KpiMetrics.RecommendationViewed, 100), T("control", KpiMetrics.RecommendationClicked, 10), .. onlyPersonalized];
        KpiCalculator.Compute(both, "personalized").ShouldNotContainKey("central_ctr_ratio");
    }

    [Fact]
    public void Satisfaction_counts_the_personalized_cohort_unless_one_is_asked_for()
    {
        KpiTotal[] totals = [T("personalized", KpiMetrics.Liked, 70), T("personalized", KpiMetrics.Disliked, 30), T("control", KpiMetrics.Liked, 10), T("control", KpiMetrics.Disliked, 30)];

        KpiCalculator.Compute(totals, null)["satisfaction"].ShouldBe(new KpiValue(0.7, 100));
        KpiCalculator.Compute(totals, "control")["satisfaction"].ShouldBe(new KpiValue(0.25, 40));
    }

    [Fact]
    public void The_creator_indicators_are_a_click_rate_a_follow_rate_and_a_count_of_attributed_installs()
    {
        KpiTotal[] totals =
        [
            T("personalized", KpiMetrics.CreatorCardViewed, 200), T("personalized", KpiMetrics.CreatorContentOpened, 50), T("control", KpiMetrics.CreatorCardViewed, 100), T("control", KpiMetrics.CreatorContentOpened, 5),
            T("personalized", KpiMetrics.CreatorProfileViewed, 40), T("personalized", KpiMetrics.CreatorFollowed, 10),
            T("personalized", KpiMetrics.Installs, 60), T("control", KpiMetrics.Installs, 40), T("personalized", KpiMetrics.InstallAttributed, 12),
        ];

        var all = KpiCalculator.Compute(totals, null);
        all["creator_block_ctr"].ShouldBe(new KpiValue(55d / 300, 300));
        all["creator_follow_rate"].ShouldBe(new KpiValue(0.25, 40));
        all["creator_attributed_installs"].ShouldBe(new KpiValue(12, 100));
        KpiCalculator.Compute(totals, "control")["creator_block_ctr"].ShouldBe(new KpiValue(0.05, 100));
        KpiCalculator.Compute(totals, "control").ShouldNotContainKey("creator_follow_rate");
        KpiCalculator.Compute(totals, "control").ShouldNotContainKey("creator_attributed_installs");
    }

    [Fact]
    public void Nothing_is_invented_for_creators_without_any_event()
    {
        var values = KpiCalculator.Compute([T("personalized", KpiMetrics.Installs, 10)], null);

        values.Keys.ShouldNotContain(key => key.StartsWith("creator_", StringComparison.Ordinal));
    }

    [Fact]
    public void The_funnel_indicators_are_ratios_of_their_components()
    {
        var values = KpiCalculator.Compute(
        [
            T("personalized", KpiMetrics.Installs, 50), T("personalized", KpiMetrics.Activated, 33), T("control", KpiMetrics.Installs, 50), T("control", KpiMetrics.Activated, 27),
            T("personalized", KpiMetrics.Sessions, 40), T("control", KpiMetrics.Sessions, 40), T("personalized", KpiMetrics.StoriesStarted, 100), T("control", KpiMetrics.StoriesStarted, 60),
            T("personalized", KpiMetrics.StoriesCompleted, 96), T("control", KpiMetrics.StoriesCompleted, 24), T("personalized", KpiMetrics.SessionsWithCrash, 1),
            T("personalized", KpiMetrics.RetentionBase(7), 20), T("personalized", KpiMetrics.RetentionReturned(7), 5),
        ], null);

        values["activation"].Value!.Value.ShouldBe(0.6, 1e-9);
        values["activation"].Sample.ShouldBe(100);
        values["stories_per_session"].Value!.Value.ShouldBe(2.0, 1e-9);
        values["completion"].Value!.Value.ShouldBe(0.75, 1e-9);
        values["crash_rate"].Value!.Value.ShouldBe(1d / 80, 1e-9);
        values["retention_d7"].Value!.Value.ShouldBe(0.25, 1e-9);
        values.ShouldNotContainKey("retention_d1"); // no installer has completed day 1 yet: no value rather than a zero
    }

    [Fact]
    public void The_click_rate_is_given_for_each_profile_depth_tranche()
    {
        var values = KpiCalculator.Compute(
        [
            T("personalized", KpiMetrics.ViewedInBand("0_9"), 100), T("personalized", KpiMetrics.ClickedInBand("0_9"), 5),
            T("personalized", KpiMetrics.ViewedInBand("10_49"), 100), T("personalized", KpiMetrics.ClickedInBand("10_49"), 9),
            T("personalized", KpiMetrics.ViewedInBand("50_plus"), 50), T("personalized", KpiMetrics.ClickedInBand("50_plus"), 8),
            T("control", KpiMetrics.ViewedInBand("0_9"), 100), T("control", KpiMetrics.ClickedInBand("0_9"), 50),
        ], null);

        values["ctr_depth_0_9"].Value!.Value.ShouldBe(0.05, 1e-9);
        values["ctr_depth_10_49"].Value!.Value.ShouldBe(0.09, 1e-9);
        values["ctr_depth_50_plus"].Value!.Value.ShouldBe(0.16, 1e-9);
    }

    [Theory]
    [InlineData(new[] { 3, 7, 20 }, 7)]
    [InlineData(new[] { 3, 7, 20, 30 }, 13.5)]
    [InlineData(new[] { 12 }, 12)]
    public void The_median_depth_comes_from_the_histogram(int[] depths, double median)
    {
        var totals = depths.GroupBy(d => d).Select(g => T("personalized", KpiMetrics.DepthHistogramPrefix + g.Key, g.Count())).ToList();

        var value = KpiCalculator.Compute(totals, null)["profile_depth_median_d7"];

        value.Value!.Value.ShouldBe(median, 1e-9);
        value.Sample.ShouldBe(depths.Length);
    }

    [Fact]
    public void Nothing_is_computed_from_nothing() => KpiCalculator.Compute([], null).ShouldBeEmpty();
}
