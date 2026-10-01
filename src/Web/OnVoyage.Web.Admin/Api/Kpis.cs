namespace OnVoyage.Web.Admin.Api;

/// <summary>
/// What <c>GET /api/insights/v1/kpis</c> returns: one value per indicator key, with the size of the sample it rests on. The back office
/// owns the catalogue of indicators and targets (§26, <see cref="KpiCatalog"/>); Insights only has to compute the values.
/// </summary>
public sealed record KpiReport(DateOnly From, DateOnly To, string? Destination, string? Cohort, IReadOnlyDictionary<string, KpiReading> Values);

public sealed record KpiReading(double? Value, long Sample);

public enum KpiUnit
{
    Ratio,
    Percent,
    Number,
    Milliseconds,
}

public enum TargetKind
{
    None,
    AtLeast,
    AtMost,
}

public enum KpiStatus
{
    /// <summary>No value was computed (not enough events, or the indicator is not produced yet).</summary>
    NoData,

    /// <summary>The sample is smaller than the minimum the indicator needs to mean anything (e.g. 1 000 impressions per cohort).</summary>
    SampleTooSmall,

    Tracked,
    Met,
    Missed,
}

public sealed record KpiDefinition(string Key, string Family, string Label, KpiUnit Unit, TargetKind TargetKind, double? Target, long MinSample = 0, string? Hint = null);

public static class KpiCatalog
{
    public const string CentralRatio = "central_ctr_ratio";
    public const string DepthLow = "ctr_depth_0_9";
    public const string DepthMid = "ctr_depth_10_49";
    public const string DepthHigh = "ctr_depth_50_plus";

    public static IReadOnlyList<KpiDefinition> All { get; } =
    [
        new(CentralRatio, "Hypothèse centrale", "Taux de clic « Pour vous » / « Incontournables »", KpiUnit.Ratio, TargetKind.AtLeast, 1.5, 1000, "Cohorte témoin de 20 %, au moins 1 000 impressions par cohorte."),
        new("satisfaction", "Satisfaction", "J'aime / (J'aime + Je n'aime pas), « Pour vous »", KpiUnit.Percent, TargetKind.AtLeast, 0.70),
        new("activation", "Activation", "Installations avec une première histoire écoutée", KpiUnit.Percent, TargetKind.AtLeast, 0.60),
        new("stories_per_session", "Engagement", "Histoires écoutées par session", KpiUnit.Number, TargetKind.AtLeast, 2),
        new("completion", "Complétion", "Écoutes allant à 80 % ou plus", KpiUnit.Percent, TargetKind.AtLeast, 0.60),
        new("retention_d1", "Rétention", "Retour à J1", KpiUnit.Percent, TargetKind.None, null),
        new("retention_d7", "Rétention", "Retour à J7", KpiUnit.Percent, TargetKind.None, null),
        new("retention_d30", "Rétention", "Retour à J30", KpiUnit.Percent, TargetKind.None, null),
        new(DepthLow, "KPI stratégique", "Taux de clic, profil de 0 à 9 signaux", KpiUnit.Percent, TargetKind.None, null, 0, "Doit croître d'une tranche à l'autre."),
        new(DepthMid, "KPI stratégique", "Taux de clic, profil de 10 à 49 signaux", KpiUnit.Percent, TargetKind.None, null),
        new(DepthHigh, "KPI stratégique", "Taux de clic, profil de 50 signaux ou plus", KpiUnit.Percent, TargetKind.None, null),
        new("profile_depth_median_d7", "Profil", "Profondeur de profil médiane à J7", KpiUnit.Number, TargetKind.AtLeast, 15),
        new("hidden_gem_listens", "Éthique", "Écoutes sur des lieux pépites", KpiUnit.Percent, TargetKind.AtLeast, 0.25),
        new("alternative_clicks", "Éthique", "Clics sur les cartes « Alternative »", KpiUnit.Number, TargetKind.None, null),
        new("inaccurate_reports_per_100", "Qualité", "Signalements « fait inexact » pour 100 écoutes", KpiUnit.Number, TargetKind.AtMost, 0.5),
        new("crash_rate", "Technique", "Sessions avec plantage", KpiUnit.Percent, TargetKind.AtMost, 0.01),
        new("latency_p95_ms", "Technique", "Latence P95 de l'API", KpiUnit.Milliseconds, TargetKind.None, null),
        new("creators_active", "Créateurs", "Créateurs fondateurs actifs", KpiUnit.Number, TargetKind.AtLeast, 5),
        new("places_with_creator_content", "Créateurs", "Lieux publiés avec au moins un contenu créateur", KpiUnit.Percent, TargetKind.AtLeast, 0.30),
    ];

    public static KpiStatus Evaluate(KpiDefinition definition, KpiReading? reading)
    {
        if (reading?.Value is not { } value || double.IsNaN(value))
        {
            return KpiStatus.NoData;
        }

        if (reading.Sample < definition.MinSample)
        {
            return KpiStatus.SampleTooSmall;
        }

        return (definition.TargetKind, definition.Target) switch
        {
            (TargetKind.AtLeast, { } target) => value >= target ? KpiStatus.Met : KpiStatus.Missed,
            (TargetKind.AtMost, { } target) => value <= target ? KpiStatus.Met : KpiStatus.Missed,
            _ => KpiStatus.Tracked,
        };
    }

    /// <summary>The strategic KPI: the click rate must rise from the thin profiles to the rich ones. Null when a tranche has no value.</summary>
    public static bool? DepthRisesWithProfile(IReadOnlyDictionary<string, KpiReading> values)
    {
        if (values.GetValueOrDefault(DepthLow)?.Value is not { } low || values.GetValueOrDefault(DepthMid)?.Value is not { } mid || values.GetValueOrDefault(DepthHigh)?.Value is not { } high)
        {
            return null;
        }

        return low < mid && mid < high;
    }

    public static string Format(KpiDefinition definition, double value) => definition.Unit switch
    {
        KpiUnit.Percent => (value * 100).ToString(value < 0.1 ? "0.0" : "0", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")) + " %",
        KpiUnit.Ratio => value.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")),
        KpiUnit.Milliseconds => value.ToString("0", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")) + " ms",
        _ => value.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")),
    };

    public static string FormatTarget(KpiDefinition definition) => definition.Target is not { } target
        ? "suivi"
        : (definition.TargetKind == TargetKind.AtMost ? "< " : "≥ ") + Format(definition, target);
}
