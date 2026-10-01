namespace OnVoyage.Web.Public.Seo;

internal static class Labels
{
    public static string Category(string code) => code switch
    {
        "history" => "Histoire",
        "architecture" => "Architecture",
        "nature" => "Nature",
        "culture" => "Culture et arts",
        "religion" => "Patrimoine religieux",
        "villages" => "Villages",
        "gastronomy" => "Gastronomie",
        "curiosities" => "Curiosités",
        "outdoors" => "Plein air",
        "leisure" => "Plages et détente",
        _ => code,
    };
}
