namespace OnVoyage.Web.Studio.Text;

/// <summary>French labels for the codes the API reports in English.</summary>
public static class Labels
{
    public static string CreatorStatus(string status) => status switch
    {
        "draft" => "Brouillon (non publié)",
        "published" => "En ligne",
        "suspended" => "Suspendu par l'équipe",
        _ => status,
    };

    /// <summary>Why the page cannot be published yet, in words for the creator. Null when nothing blocks.</summary>
    public static string? PublishBlock(string? code) => code switch
    {
        null => null,
        "terms_required" => "Publication impossible : les CGU créateurs ne sont pas acceptées.",
        "specialty_required" => "Publication impossible : choisissez au moins une spécialité.",
        _ => "Publication impossible : le profil n'est pas complet.",
    };

    public static string Platform(string platform) => platform switch
    {
        "youtube" => "YouTube",
        "instagram" => "Instagram",
        "tiktok" => "TikTok",
        _ => platform,
    };

    public static string ContentStatus(string status) => status switch
    {
        "imported" => "En ligne",
        "hidden" => "Masqué",
        "removed" => "Retiré",
        _ => status,
    };

    public static string LinkStatus(string status) => status switch
    {
        "validated" => "Validée (publiée)",
        "proposed" => "Proposée (non publiée)",
        "rejected" => "Refusée",
        _ => status,
    };

    /// <summary>The ten level-1 categories of the taxonomy (annexe D) in French, for the specialties of a creator.</summary>
    public static string Specialty(string code) => code switch
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
        "leisure" => "Loisirs",
        _ => code,
    };
}
