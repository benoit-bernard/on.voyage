namespace OnVoyage.Insights.Domain;

/// <summary>
/// Names of the daily components stored in <c>insights.daily_kpi</c>. The table keeps counts, not ratios, so any period can be summed and
/// the ratio computed afterwards (<see cref="KpiCalculator"/>). Install-based components (<c>installs</c>, <c>activated</c>, <c>retention_*</c>,
/// <c>profile_depth_d7_h*</c>) sit on the day of the first visit, the others on the day of the event.
/// </summary>
public static class KpiMetrics
{
    public const string RecommendationViewed = "rec_viewed";
    public const string RecommendationClicked = "rec_clicked";
    public const string Liked = "poi_liked";
    public const string Disliked = "poi_disliked";
    public const string StoriesStarted = "audio_started";
    public const string StoriesCompleted = "audio_completed_80";
    public const string Sessions = "sessions";
    public const string SessionsWithCrash = "sessions_with_crash";
    public const string Installs = "installs";
    public const string Activated = "activated";
    public const string DepthHistogramPrefix = "profile_depth_d7_h";

    public static string ViewedInBand(string band) => $"rec_viewed_depth_{band}";

    public static string ClickedInBand(string band) => $"rec_clicked_depth_{band}";

    public static string RetentionBase(int days) => $"retention_d{days}_base";

    public static string RetentionReturned(int days) => $"retention_d{days}_returned";

    public static IReadOnlyList<int> RetentionDays { get; } = [1, 7, 30];

    /// <summary>The ProfileDepth tranches of the strategic KPI (§26).</summary>
    public static IReadOnlyList<string> DepthBands { get; } = ["0_9", "10_49", "50_plus"];
}

/// <summary>Reading of one indicator: the value and the size of the sample it rests on (impressions, sessions, installs…).</summary>
public sealed record KpiValue(double? Value, long Sample);

/// <summary>One stored component: a cohort, a metric name and its total over the period.</summary>
public sealed record KpiTotal(string Cohort, string Metric, double Value);

/// <summary>
/// Turns the daily components into the indicators of §26. Pure and deterministic. Indicators that cannot be computed (no denominator) are
/// absent, which the back office shows as "no data". Where the cahier speaks of « Pour vous » (satisfaction, click rate by depth), the
/// personalized cohort is used unless a cohort is asked for.
/// </summary>
public static class KpiCalculator
{
    public const string Control = "control";
    public const string Personalized = "personalized";

    public static IReadOnlyDictionary<string, KpiValue> Compute(IReadOnlyCollection<KpiTotal> totals, string? cohort)
    {
        var result = new Dictionary<string, KpiValue>(StringComparer.Ordinal);
        double Sum(string metric, string? only) => totals.Where(t => t.Metric == metric && (only is null || t.Cohort == only)).Sum(t => t.Value);
        void Ratio(string key, string numerator, string denominator, string? only)
        {
            var below = Sum(denominator, only);
            if (below > 0)
            {
                result[key] = new KpiValue(Sum(numerator, only) / below, (long)below);
            }
        }

        // Central hypothesis: click rate of the personalized cohort over the click rate of the control cohort. Needs both arms.
        if (cohort is null)
        {
            var personalizedViews = Sum(KpiMetrics.RecommendationViewed, Personalized);
            var controlViews = Sum(KpiMetrics.RecommendationViewed, Control);
            var controlRate = controlViews > 0 ? Sum(KpiMetrics.RecommendationClicked, Control) / controlViews : 0;
            if (personalizedViews > 0 && controlRate > 0)
            {
                result["central_ctr_ratio"] = new KpiValue(Sum(KpiMetrics.RecommendationClicked, Personalized) / personalizedViews / controlRate, (long)Math.Min(personalizedViews, controlViews));
            }
        }

        var forYou = cohort ?? Personalized;
        var likes = Sum(KpiMetrics.Liked, forYou);
        var judged = likes + Sum(KpiMetrics.Disliked, forYou);
        if (judged > 0)
        {
            result["satisfaction"] = new KpiValue(likes / judged, (long)judged);
        }

        Ratio("activation", KpiMetrics.Activated, KpiMetrics.Installs, cohort);
        Ratio("stories_per_session", KpiMetrics.StoriesStarted, KpiMetrics.Sessions, cohort);
        Ratio("completion", KpiMetrics.StoriesCompleted, KpiMetrics.StoriesStarted, cohort);
        Ratio("crash_rate", KpiMetrics.SessionsWithCrash, KpiMetrics.Sessions, cohort);
        foreach (var days in KpiMetrics.RetentionDays)
        {
            Ratio($"retention_d{days}", KpiMetrics.RetentionReturned(days), KpiMetrics.RetentionBase(days), cohort);
        }

        foreach (var band in KpiMetrics.DepthBands)
        {
            Ratio($"ctr_depth_{band}", KpiMetrics.ClickedInBand(band), KpiMetrics.ViewedInBand(band), forYou);
        }

        if (MedianDepth(totals, cohort) is { } median)
        {
            result["profile_depth_median_d7"] = median;
        }

        return result;
    }

    /// <summary>Median of the histogram <c>profile_depth_d7_h&lt;depth&gt;</c> (a median cannot be summed over days, a histogram can).</summary>
    private static KpiValue? MedianDepth(IReadOnlyCollection<KpiTotal> totals, string? cohort)
    {
        var histogram = new SortedDictionary<int, double>();
        foreach (var total in totals.Where(t => t.Metric.StartsWith(KpiMetrics.DepthHistogramPrefix, StringComparison.Ordinal) && (cohort is null || t.Cohort == cohort)))
        {
            if (int.TryParse(total.Metric.AsSpan(KpiMetrics.DepthHistogramPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var depth))
            {
                histogram[depth] = histogram.GetValueOrDefault(depth) + total.Value;
            }
        }

        var count = histogram.Values.Sum();
        if (count <= 0)
        {
            return null;
        }

        double AtRank(double rank)
        {
            var seen = 0d;
            foreach (var (depth, size) in histogram)
            {
                seen += size;
                if (seen >= rank)
                {
                    return depth;
                }
            }

            return histogram.Keys.Last();
        }

        var median = count % 2 == 0 ? (AtRank(count / 2) + AtRank((count / 2) + 1)) / 2 : AtRank((count + 1) / 2);
        return new KpiValue(median, (long)count);
    }
}
