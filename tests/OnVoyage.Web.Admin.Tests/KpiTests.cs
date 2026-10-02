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
        KpiCatalog.Evaluate(Definition("crash_rate"), new KpiReading(null, 0)).ShouldBe(KpiStatus.NoData);
        KpiCatalog.Evaluate(Definition("crash_rate"), null).ShouldBe(KpiStatus.NoData);
        KpiCatalog.Evaluate(Definition("retention_d7"), new KpiReading(0.2, 10)).ShouldBe(KpiStatus.Tracked);
    }

    [Fact]
    public void An_indicator_that_does_not_come_from_insights_is_not_produced_rather_than_missing_data()
    {
        var latency = Definition("latency_p95_ms");

        latency.Source.ShouldNotBeNull();
        KpiCatalog.Evaluate(latency, null).ShouldBe(KpiStatus.NotProduced);
        KpiCatalog.Evaluate(latency, new KpiReading(null, 0)).ShouldBe(KpiStatus.NotProduced);
        KpiCatalog.Evaluate(latency, new KpiReading(180, 1000)).ShouldBe(KpiStatus.Tracked, "if a value ever arrives it is shown");
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

    /// <summary>Insights answers a different report for each cohort asked, as it does.</summary>
    private IRenderedComponent<Kpis> ShowCohorts(KpiReport all, KpiReport personalized, KpiReport control)
    {
        Services.AddSingleton(_api);
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)));
        _api.GetDestinationsAsync(Arg.Any<CancellationToken>()).Returns([new DestinationItem("marseille", "Marseille", 43.3, 5.4)]);
        _api.GetKpisAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(all);
        _api.GetKpisAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<string?>(), "personalized", Arg.Any<CancellationToken>()).Returns(personalized);
        _api.GetKpisAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<string?>(), "control", Arg.Any<CancellationToken>()).Returns(control);
        return Render<Kpis>();
    }

    private static KpiReport CohortReport(string cohort, params (string Key, double? Value, long Sample)[] readings) =>
        new(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), null, cohort, readings.ToDictionary(item => item.Key, item => new KpiReading(item.Value, item.Sample)));

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

    // ---- the dashboard against what Insights really computes and sends

    [Fact]
    public void The_catalogue_lists_exactly_the_indicators_insights_computes_plus_the_ones_it_cannot()
    {
        // A fixture with every component Insights stores, so KpiCalculator produces every indicator it can.
        List<OnVoyage.Insights.Domain.KpiTotal> totals = [];
        foreach (var cohort in OnVoyage.Insights.Contracts.Cohorts.All)
        {
            foreach (var metric in new[]
            {
                OnVoyage.Insights.Domain.KpiMetrics.RecommendationViewed, OnVoyage.Insights.Domain.KpiMetrics.RecommendationClicked, OnVoyage.Insights.Domain.KpiMetrics.Liked, OnVoyage.Insights.Domain.KpiMetrics.Disliked,
                OnVoyage.Insights.Domain.KpiMetrics.StoriesStarted, OnVoyage.Insights.Domain.KpiMetrics.StoriesCompleted, OnVoyage.Insights.Domain.KpiMetrics.Sessions, OnVoyage.Insights.Domain.KpiMetrics.SessionsWithCrash,
                OnVoyage.Insights.Domain.KpiMetrics.Installs, OnVoyage.Insights.Domain.KpiMetrics.Activated, OnVoyage.Insights.Domain.KpiMetrics.CreatorCardViewed, OnVoyage.Insights.Domain.KpiMetrics.CreatorContentOpened,
                OnVoyage.Insights.Domain.KpiMetrics.CreatorProfileViewed, OnVoyage.Insights.Domain.KpiMetrics.CreatorFollowed, OnVoyage.Insights.Domain.KpiMetrics.InstallAttributed,
                $"{OnVoyage.Insights.Domain.KpiMetrics.DepthHistogramPrefix}12",
            })
            {
                totals.Add(new(cohort, metric, 10));
            }

            foreach (var days in OnVoyage.Insights.Domain.KpiMetrics.RetentionDays)
            {
                totals.Add(new(cohort, OnVoyage.Insights.Domain.KpiMetrics.RetentionBase(days), 10));
                totals.Add(new(cohort, OnVoyage.Insights.Domain.KpiMetrics.RetentionReturned(days), 4));
            }

            foreach (var band in OnVoyage.Insights.Domain.KpiMetrics.DepthBands)
            {
                totals.Add(new(cohort, OnVoyage.Insights.Domain.KpiMetrics.ViewedInBand(band), 10));
                totals.Add(new(cohort, OnVoyage.Insights.Domain.KpiMetrics.ClickedInBand(band), 2));
            }
        }

        var computed = OnVoyage.Insights.Domain.KpiCalculator.Compute(totals, null).Keys.ToHashSet();

        computed.Except(KpiCatalog.All.Select(item => item.Key)).ShouldBeEmpty("an indicator Insights computes must appear on the dashboard");
        KpiCatalog.All.Where(item => item.Source is null).Select(item => item.Key).Except(computed).ShouldBeEmpty("an indicator without a source must be one Insights computes");
        KpiCatalog.All.Where(item => item.Source is not null).Select(item => item.Key).Intersect(computed).ShouldBeEmpty();
    }

    [Fact]
    public void The_catalogue_has_every_indicator_of_section_26_with_its_target()
    {
        KpiCatalog.All.Select(item => item.Key).ShouldBe(
        [
            "central_ctr_ratio", "satisfaction", "activation", "stories_per_session", "completion", "retention_d1", "retention_d7", "retention_d30",
            "ctr_depth_0_9", "ctr_depth_10_49", "ctr_depth_50_plus", "profile_depth_median_d7", "hidden_gem_listens", "alternative_clicks",
            "inaccurate_reports_per_100", "crash_rate", "latency_p95_ms", "creators_active", "places_with_creator_content", "creator_block_ctr", "creator_follow_rate", "creator_attributed_installs",
        ], ignoreOrder: true);
        Definition(KpiCatalog.CentralRatio).Target.ShouldBe(1.5);
        Definition("satisfaction").Target.ShouldBe(0.70);
        Definition("activation").Target.ShouldBe(0.60);
        Definition("stories_per_session").Target.ShouldBe(2);
        Definition("completion").Target.ShouldBe(0.60);
        Definition("profile_depth_median_d7").Target.ShouldBe(15);
        Definition("hidden_gem_listens").Target.ShouldBe(0.25);
        Definition("inaccurate_reports_per_100").Target.ShouldBe(0.5);
        Definition("crash_rate").Target.ShouldBe(0.01);
    }

    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public void The_report_insights_sends_is_read_as_it_is()
    {
        var sent = new OnVoyage.Insights.Contracts.KpiReportDto(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), null, "personalized",
            new Dictionary<string, OnVoyage.Insights.Contracts.KpiReadingDto> { ["activation"] = new(0.7, 40), ["retention_d30"] = new(null, 0) });

        var read = System.Text.Json.JsonSerializer.Deserialize<KpiReport>(System.Text.Json.JsonSerializer.Serialize(sent, Web), Web)!;

        read.Cohort.ShouldBe("personalized");
        read.Values["activation"].ShouldBe(new KpiReading(0.7, 40));
        read.Values["retention_d30"].Value.ShouldBeNull();
    }

    [Fact]
    public void The_cohort_choices_use_the_names_insights_accepts()
    {
        // Insights refuses any other spelling with a 400 ("cohort must be 'control' or 'personalized'").
        var cut = Show(Report());
        cut.WaitForAssertion(() => cut.FindAll("#cohort option").ShouldNotBeEmpty());

        var values = cut.FindAll("#cohort option").Select(option => option.GetAttribute("value")!).Where(value => value.Length > 0).ToArray();

        values.ShouldBe(OnVoyage.Insights.Contracts.Cohorts.All, ignoreOrder: true);
    }

    [Fact]
    public void The_two_cohorts_are_compared_side_by_side_with_the_gap_in_points()
    {
        var cut = ShowCohorts(
            all: Report((KpiCatalog.CentralRatio, 1.5, 1200)),
            personalized: CohortReport("personalized", ("activation", 0.72, 200), ("completion", 0.66, 400), ("stories_per_session", 2.4, 300), ("creator_attributed_installs", 12, 200)),
            control: CohortReport("control", ("activation", 0.5, 50), ("completion", 0.7, 100), ("stories_per_session", 1.9, 80)));

        cut.WaitForAssertion(() =>
        {
            string Row(string key) => cut.Find($"#comparison tr[data-key={key}]").TextContent;
            Row("activation").ShouldContain("72 %");
            Row("activation").ShouldContain("50 %");
            Row("activation").ShouldContain("+22,0 pts");
            Row("completion").ShouldContain("−4,0 pts");
            Row("stories_per_session").ShouldContain("+0,5");
            Row("creator_attributed_installs").ShouldContain("12");
            cut.FindAll("#comparison tr[data-key=central_ctr_ratio]").ShouldBeEmpty("the central ratio compares the cohorts itself");
            cut.FindAll("#comparison tr[data-key=latency_p95_ms]").ShouldBeEmpty("an indicator with no value in either cohort is not compared");
        });
    }

    [Fact]
    public void Choosing_one_cohort_shows_only_that_cohort_and_no_comparison()
    {
        var cut = ShowCohorts(all: Report(), personalized: CohortReport("personalized", ("activation", 0.7, 10)), control: CohortReport("control", ("activation", 0.5, 10)));
        cut.WaitForAssertion(() => cut.FindAll("#comparison").ShouldNotBeEmpty());
        _api.ClearReceivedCalls();

        cut.Find("#cohort").Change("control");
        cut.FindAll("button").First(button => button.TextContent == "Afficher").Click();

        cut.WaitForAssertion(() => cut.FindAll("#comparison").ShouldBeEmpty());
        _api.Received(1).GetKpisAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_control_cohort_without_data_says_so()
    {
        var cut = ShowCohorts(all: Report(("activation", 0.7, 10)), personalized: CohortReport("personalized", ("activation", 0.7, 10)), control: CohortReport("control"));

        cut.WaitForAssertion(() =>
        {
            cut.Find("#control-empty").TextContent.ShouldContain("témoin");
            cut.Find("#comparison tr[data-key=activation]").TextContent.ShouldContain("—");
        });
    }

    [Fact]
    public void An_empty_period_is_explained_and_a_destination_filter_gets_its_own_hint()
    {
        var cut = ShowCohorts(all: Report(), personalized: CohortReport("personalized"), control: CohortReport("control"));
        cut.WaitForAssertion(() => cut.Find("#empty").TextContent.ShouldContain("accepté les statistiques"));
        cut.FindAll("#comparison-empty").ShouldNotBeEmpty();

        cut.Find("#destination").Change("marseille");
        cut.FindAll("button").First(button => button.TextContent == "Afficher").Click();

        cut.WaitForAssertion(() => cut.Find("#empty").TextContent.ShouldContain("pas encore rattachés à une destination"));
    }

    [Fact]
    public void The_not_produced_indicators_name_their_source()
    {
        var cut = Show(Report(("activation", 0.7, 100)));

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find("tr.kpi[data-key=latency_p95_ms]");
            row.ClassList.ShouldContain("notproduced");
            row.TextContent.ShouldContain("Pas encore branché");
            row.TextContent.ShouldContain("OpenTelemetry");
            cut.Find("tr.kpi[data-key=creator_block_ctr]").TextContent.ShouldContain("Pas de données");
        });
    }

    [Fact]
    public void The_period_buttons_ask_insights_for_the_last_days()
    {
        var cut = Show(Report());
        cut.WaitForAssertion(() => cut.FindAll("button[data-days]").Count.ShouldBe(3));
        _api.ClearReceivedCalls();

        cut.Find("button[data-days='7']").Click();

        cut.WaitForAssertion(() => _api.Received().GetKpisAsync(new DateOnly(2026, 9, 24), new DateOnly(2026, 10, 1), null, null, Arg.Any<CancellationToken>()));
        cut.Find("#from").GetAttribute("value").ShouldBe("2026-09-24");
        cut.Find("#to").GetAttribute("value").ShouldBe("2026-10-01");
    }

    [Fact]
    public void A_period_longer_than_insights_accepts_is_refused_before_the_call()
    {
        var cut = Show(Report());
        cut.WaitForAssertion(() => cut.FindAll("#from").ShouldNotBeEmpty());
        _api.ClearReceivedCalls();

        cut.Find("#from").Change("2025-01-01");
        cut.FindAll("button").First(button => button.TextContent == "Afficher").Click();

        cut.Find("[role=alert]").TextContent.ShouldContain("400 jours");
        _api.DidNotReceive().GetKpisAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_refusal_from_insights_is_shown_instead_of_crashing()
    {
        Services.AddSingleton(_api);
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)));
        _api.GetDestinationsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _api.GetKpisAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<KpiReport?>>(_ => throw new AdminApiException("from must not be after to", 400));

        var cut = Render<Kpis>();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.ShouldBe("from must not be after to"));
    }

    // ---- CSV export

    [Fact]
    public void The_csv_has_one_line_per_indicator_and_cohort_with_dot_decimals_and_quoted_labels()
    {
        var csv = KpiCsv.Build(
        [
            ("toutes", Report(("activation", 0.634, 300), ("crash_rate", 0.004, 900))),
            ("personalized", CohortReport("personalized", ("activation", 0.72, 200))),
            ("control", null),
        ]);

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldBe(KpiCsv.Header);
        lines.Length.ShouldBe(1 + (2 * KpiCatalog.All.Count), "a cohort without a report adds nothing");
        lines.ShouldContain("activation,\"Activation\",\"Installations avec une première histoire écoutée\",toutes,0.634,300,0.6,met");
        lines.ShouldContain("activation,\"Activation\",\"Installations avec une première histoire écoutée\",personalized,0.72,200,0.6,met");
        lines.ShouldContain("crash_rate,\"Technique\",\"Sessions avec plantage\",toutes,0.004,900,0.01,met");
        lines.ShouldContain("latency_p95_ms,\"Technique\",\"Latence P95 de l'API\",toutes,,,,notproduced");
        lines.ShouldContain("retention_d1,\"Rétention\",\"Retour à J1\",personalized,,,,nodata");
    }

    [Fact]
    public void The_export_link_carries_the_same_csv_under_a_dated_file_name()
    {
        var cut = ShowCohorts(all: Report(("activation", 0.634, 300)), personalized: CohortReport("personalized", ("activation", 0.72, 200)), control: CohortReport("control"));

        cut.WaitForAssertion(() =>
        {
            var link = cut.Find("a#export");
            link.GetAttribute("download").ShouldBe("indicateurs-20260901-20261001.csv");
            var href = link.GetAttribute("href")!;
            href.ShouldStartWith("data:text/csv;charset=utf-8;base64,");
            var bytes = Convert.FromBase64String(href["data:text/csv;charset=utf-8;base64,".Length..]);
            bytes[..3].ShouldBe(System.Text.Encoding.UTF8.GetPreamble(), "a byte-order mark so a spreadsheet reads the accents");
            var text = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            text.ShouldStartWith(KpiCsv.Header);
            text.ShouldContain("activation,\"Activation\",\"Installations avec une première histoire écoutée\",toutes,0.634,300");
            text.ShouldContain(",personalized,0.72,200");
        });
    }

    [Fact]
    public void Without_insights_there_is_nothing_to_export()
    {
        var cut = Show(null);

        cut.WaitForAssertion(() => cut.FindAll("[role=status]").ShouldNotBeEmpty());
        cut.FindAll("a#export").ShouldBeEmpty();
    }

    [Fact]
    public void The_gap_between_cohorts_is_written_in_the_unit_of_the_indicator()
    {
        KpiCatalog.FormatGap(Definition("activation"), new KpiReading(0.72, 1), new KpiReading(0.5, 1)).ShouldBe("+22,0 pts");
        KpiCatalog.FormatGap(Definition("activation"), new KpiReading(0.5, 1), new KpiReading(0.72, 1)).ShouldBe("−22,0 pts");
        KpiCatalog.FormatGap(Definition("activation"), new KpiReading(0.5, 1), new KpiReading(0.5, 1)).ShouldBe("0,0 pts");
        KpiCatalog.FormatGap(Definition("stories_per_session"), new KpiReading(2.4, 1), new KpiReading(1.9, 1)).ShouldBe("+0,5");
        KpiCatalog.FormatGap(Definition("activation"), new KpiReading(0.5, 1), null).ShouldBeNull();
        KpiCatalog.FormatGap(Definition("activation"), new KpiReading(null, 0), new KpiReading(0.5, 1)).ShouldBeNull();
    }
}

