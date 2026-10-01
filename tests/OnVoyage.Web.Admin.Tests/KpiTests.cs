using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.Web.Admin.Api;
using OnVoyage.Web.Admin.Components.Pages;

namespace OnVoyage.Web.Admin.Tests;

public sealed class KpiTests : BunitContext
{
    private readonly IAdminApi _api = Substitute.For<IAdminApi>();

    private static KpiDefinition Definition(string key) => KpiCatalog.All.Single(item => item.Key == key);

    private static KpiReport Report(params (string Key, double? Value, long Sample)[] readings) =>
        new(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), null, null, readings.ToDictionary(item => item.Key, item => new KpiReading(item.Value, item.Sample)));

    [Theory]
    [InlineData(1.62, 1450, KpiStatus.Met)]
    [InlineData(1.49, 1450, KpiStatus.Missed)]
    [InlineData(1.50, 1000, KpiStatus.Met)]
    [InlineData(2.30, 999, KpiStatus.SampleTooSmall)] // a good ratio on too few impressions proves nothing
    public void The_central_hypothesis_needs_a_ratio_of_1_5_on_at_least_1000_impressions(double value, long impressions, KpiStatus expected) =>
        KpiCatalog.Evaluate(Definition(KpiCatalog.CentralRatio), new KpiReading(value, impressions)).ShouldBe(expected);

    [Fact]
    public void A_maximum_target_is_met_below_the_threshold_and_a_missing_value_is_no_data()
    {
        var reports = Definition("inaccurate_reports_per_100");

        KpiCatalog.Evaluate(reports, new KpiReading(0.3, 500)).ShouldBe(KpiStatus.Met);
        KpiCatalog.Evaluate(reports, new KpiReading(0.5, 500)).ShouldBe(KpiStatus.Met);
        KpiCatalog.Evaluate(reports, new KpiReading(0.9, 500)).ShouldBe(KpiStatus.Missed);
        KpiCatalog.Evaluate(reports, new KpiReading(null, 0)).ShouldBe(KpiStatus.NoData);
        KpiCatalog.Evaluate(reports, null).ShouldBe(KpiStatus.NoData);
        KpiCatalog.Evaluate(Definition("retention_d7"), new KpiReading(0.2, 10)).ShouldBe(KpiStatus.Tracked);
    }

    [Fact]
    public void The_strategic_kpi_must_rise_from_thin_to_rich_profiles()
    {
        KpiCatalog.DepthRisesWithProfile(Report((KpiCatalog.DepthLow, 0.05, 1), (KpiCatalog.DepthMid, 0.08, 1), (KpiCatalog.DepthHigh, 0.11, 1)).Values).ShouldBe(true);
        KpiCatalog.DepthRisesWithProfile(Report((KpiCatalog.DepthLow, 0.05, 1), (KpiCatalog.DepthMid, 0.04, 1), (KpiCatalog.DepthHigh, 0.11, 1)).Values).ShouldBe(false);
        KpiCatalog.DepthRisesWithProfile(Report((KpiCatalog.DepthLow, 0.05, 1), (KpiCatalog.DepthMid, 0.08, 1)).Values).ShouldBeNull();
    }

    [Fact]
    public void Every_indicator_has_a_unique_key_and_the_catalogue_covers_the_section_26_families()
    {
        KpiCatalog.All.Select(item => item.Key).ShouldBeUnique();
        KpiCatalog.All.Select(item => item.Family).Distinct().ShouldBe(
            ["Hypothèse centrale", "Satisfaction", "Activation", "Engagement", "Complétion", "Rétention", "KPI stratégique", "Profil", "Éthique", "Qualité", "Technique", "Créateurs"], ignoreOrder: true);
    }

    [Fact]
    public void Values_are_formatted_in_french()
    {
        KpiCatalog.Format(Definition("activation"), 0.634).ShouldBe("63 %");
        KpiCatalog.Format(Definition("crash_rate"), 0.004).ShouldBe("0,4 %");
        KpiCatalog.Format(Definition(KpiCatalog.CentralRatio), 1.623).ShouldBe("1,62");
        KpiCatalog.FormatTarget(Definition("crash_rate")).ShouldBe("< 1,0 %");
        KpiCatalog.FormatTarget(Definition("retention_d1")).ShouldBe("suivi");
    }

    private IRenderedComponent<Kpis> Show(KpiReport? report)
    {
        Services.AddSingleton(_api);
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)));
        _api.GetDestinationsAsync(Arg.Any<CancellationToken>()).Returns([new DestinationItem("marseille", "Marseille", 43.3, 5.4)]);
        _api.GetKpisAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(report);
        return Render<Kpis>();
    }

    [Fact]
    public void The_dashboard_compares_fixed_data_with_the_targets()
    {
        var cut = Show(Report(
            (KpiCatalog.CentralRatio, 1.62, 1450), ("activation", 0.48, 300), ("crash_rate", 0.004, 900),
            (KpiCatalog.DepthLow, 0.05, 200), (KpiCatalog.DepthMid, 0.08, 200), (KpiCatalog.DepthHigh, 0.11, 200)));

        cut.WaitForAssertion(() =>
        {
            string Row(string label) => cut.FindAll("tbody tr").First(row => row.TextContent.Contains(label, StringComparison.Ordinal)).TextContent;
            Row("« Pour vous » / « Incontournables »").ShouldContain("1,62");
            Row("« Pour vous » / « Incontournables »").ShouldContain("Atteinte");
            Row("première histoire écoutée").ShouldContain("48 %");
            Row("première histoire écoutée").ShouldContain("Non atteinte");
            Row("plantage").ShouldContain("Atteinte");
            Row("Retour à J1").ShouldContain("Pas de données");
            cut.Find("#depth-trend").TextContent.ShouldContain("croît bien");
        });
    }

    [Fact]
    public void A_good_ratio_on_a_small_sample_is_flagged_and_not_counted_as_met()
    {
        var cut = Show(Report((KpiCatalog.CentralRatio, 2.4, 400)));

        cut.WaitForAssertion(() => cut.FindAll("tr.kpi.sampletoosmall").Count.ShouldBe(1));
    }

    [Fact]
    public void Without_insights_the_page_says_so_and_still_lists_the_indicators()
    {
        var cut = Show(null);

        cut.WaitForAssertion(() =>
        {
            cut.Find("[role=status]").TextContent.ShouldContain("Insights n'est pas encore branché");
            cut.FindAll("tbody tr").Count.ShouldBe(KpiCatalog.All.Count);
        });
    }

    [Fact]
    public void Asking_for_a_period_that_ends_before_it_starts_is_refused_without_calling_insights()
    {
        var cut = Show(Report());
        cut.WaitForAssertion(() => cut.FindAll("#from").ShouldNotBeEmpty());
        _api.ClearReceivedCalls();

        cut.Find("#from").Change("2026-11-01");
        cut.FindAll("button").First(button => button.TextContent == "Afficher").Click();

        cut.Find("[role=alert]").TextContent.ShouldContain("précéder");
        _api.DidNotReceive().GetKpisAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void The_chosen_filters_are_sent_to_insights()
    {
        var cut = Show(Report());
        cut.WaitForAssertion(() => cut.FindAll("#cohort").ShouldNotBeEmpty());

        cut.Find("#destination").Change("marseille");
        cut.Find("#cohort").Change("control");
        cut.FindAll("button").First(button => button.TextContent == "Afficher").Click();

        _api.Received().GetKpisAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), "marseille", "control", Arg.Any<CancellationToken>());
    }
}
