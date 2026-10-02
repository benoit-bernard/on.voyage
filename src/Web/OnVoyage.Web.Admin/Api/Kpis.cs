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
    /// <summary>No value was computed for the period (not enough events, or nobody accepted the statistics).</summary>
    NoData,

    /// <summary>The indicator does not come from Insights and nothing feeds it into this dashboard yet (see <see cref="KpiDefinition.Source"/>).</summary>
    NotProduced,

    /// <summary>The sample is smaller than the minimum the indicator needs to mean anything (e.g. 1 000 impressions per cohort).</summary>
    SampleTooSmall,

    Tracked,
    Met,
    Missed,
}

/// <param name="PerCohort">False for an indicator that only makes sense across both cohorts (the central ratio): it is left out of the cohort comparison.</param>
/// <param name="Source">Where the value would come from when it is not Insights (Factory, Creators, observability): the indicator is then listed as "not produced" instead of "no data".</param>
public sealed record KpiDefinition(string Key, string Family, string Label, KpiUnit Unit, TargetKind TargetKind, double? Target, long MinSample = 0, string? Hint = null, bool PerCohort = true, string? Source = null);

/// <summary>The dashboard as a CSV file: one line per indicator and cohort, with the value, the sample behind it, the target and the verdict.</summary>
public static class KpiCsv
{
    public const string Header = "indicateur,famille,libelle,cohorte,valeur,echantillon,cible,etat";

    /// <summary>Invariant culture and dot decimals so a spreadsheet or a script reads the numbers whatever the locale; text fields are quoted.</summary>
    public static string Build(IEnumerable<(string Cohort, KpiReport? Report)> reports)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var csv = new System.Text.StringBuilder(Header).Append('\n');
        foreach (var (cohort, report) in reports.Where(item => item.Report is not null))
        {
            foreach (var definition in KpiCatalog.All)
            {
                var reading = report!.Values.GetValueOrDefault(definition.Key);
                csv.Append(definition.Key).Append(',')
                    .Append(Quote(definition.Family)).Append(',')
                    .Append(Quote(definition.Label)).Append(',')
                    .Append(cohort).Append(',')
                    .Append(reading?.Value is { } value ? value.ToString("0.####", invariant) : string.Empty).Append(',')
                    .Append(reading is null ? string.Empty : reading.Sample.ToString(invariant)).Append(',')
                    .Append(definition.Target is { } target ? target.ToString("0.####", invariant) : string.Empty).Append(',')
                    .Append(KpiCatalog.Evaluate(definition, reading).ToString().ToLowerInvariant()).Append('\n');
            }
        }

        return csv.ToString();
    }

    private static string Quote(string text) => $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

public static class KpiCatalog
{
    public const string CentralRatio = "central_ctr_ratio";
    public const string DepthLow = "ctr_depth_0_9";
    public const string DepthMid = "ctr_depth_10_49";
    public const string DepthHigh = "ctr_depth_50_plus";

    public static IReadOnlyList<KpiDefinition> All { get; } =
    [
        new(CentralRatio, "Hypothèse centrale", "Taux de clic « Pour vous » / « Incontournables »", KpiUnit.Ratio, TargetKind.AtLeast, 1.5, 1000, "Cohorte témoin de 20 %, au moins 1 000 impressions par cohorte.", PerCohort: false),
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
        new("hidden_gem_listens", "Éthique", "Écoutes sur des lieux pépites", KpiUnit.Percent, TargetKind.AtLeast, 0.25, Source: "Catalog (drapeau pépite) croisé avec les écoutes d'Insights"),
        new("alternative_clicks", "Éthique", "Clics sur les cartes « Alternative »", KpiUnit.Number, TargetKind.None, null, Source: "Insights, quand la surface « alternative » sera distinguée"),
        new("inaccurate_reports_per_100", "Qualité", "Signalements « fait inexact » pour 100 écoutes", KpiUnit.Number, TargetKind.AtMost, 0.5, Source: "Factory (signalements) rapporté aux écoutes d'Insights"),
        new("crash_rate", "Technique", "Sessions avec plantage", KpiUnit.Percent, TargetKind.AtMost, 0.01),
        new("latency_p95_ms", "Technique", "Latence P95 de l'API", KpiUnit.Milliseconds, TargetKind.None, null, Source: "Observabilité (traces OpenTelemetry, NF-01 et NF-02)"),
        new("creators_active", "Créateurs", "Créateurs fondateurs actifs", KpiUnit.Number, TargetKind.AtLeast, 5, Source: "Creators"),
        new("places_with_creator_content", "Créateurs", "Lieux publiés avec au moins un contenu créateur", KpiUnit.Percent, TargetKind.AtLeast, 0.30, Source: "Creators et Catalog"),
        new("creator_block_ctr", "Créateurs", "Taux de clic du bloc « Vu par les créateurs »", KpiUnit.Percent, TargetKind.None, null),
        new("creator_follow_rate", "Créateurs", "Taux de suivi (suivis / profils de créateurs ouverts)", KpiUnit.Percent, TargetKind.None, null),
        new("creator_attributed_installs", "Créateurs", "Installations attribuées à des liens créateurs", KpiUnit.Number, TargetKind.None, null, Hint: "Sur le nombre d'installations de la période."),
    ];

    public static KpiStatus Evaluate(KpiDefinition definition, KpiReading? reading)
    {
        if (reading?.Value is not { } value || double.IsNaN(value))
        {
            return definition.Source is null ? KpiStatus.NoData : KpiStatus.NotProduced;
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

    /// <summary>
    /// The gap between the two cohorts, in the unit of the indicator: percentage points for a percentage, the plain difference otherwise.
    /// Null when one of them has no value.
    /// </summary>
    public static string? FormatGap(KpiDefinition definition, KpiReading? personalized, KpiReading? control)
    {
        if (personalized?.Value is not { } a || control?.Value is not { } b)
        {
            return null;
        }

        var gap = a - b;
        var french = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        var sign = gap > 0 ? "+" : gap < 0 ? "−" : string.Empty;
        var size = Math.Abs(gap);
        return definition.Unit switch
        {
            KpiUnit.Percent => $"{sign}{(size * 100).ToString("0.0", french)} pts",
            KpiUnit.Milliseconds => $"{sign}{size.ToString("0", french)} ms",
            _ => $"{sign}{size.ToString("0.##", french)}",
        };
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
