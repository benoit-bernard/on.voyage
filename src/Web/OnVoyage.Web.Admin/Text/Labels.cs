namespace OnVoyage.Web.Admin.Text;

/// <summary>French labels for the statuses the API reports in English.</summary>
public static class Labels
{
    public static string Story(string status) => status switch
    {
        "Draft" => "Brouillon",
        "AiGenerated" => "Générée",
        "Checked" => "Contrôlée, à relire",
        "NeedsReview" => "À relire (contrôles en échec)",
        "Approved" => "Approuvée, voix à générer",
        "AudioReady" => "Voix prête, à écouter",
        "Published" => "Publiée",
        "Suspended" => "Suspendue",
        "Archived" => "Archivée",
        "Rejected" => "Refusée",
        "Failed" => "Échec technique",
        _ => status,
    };

    public static string Place(string status) => status switch
    {
        "Candidate" => "Candidat",
        "NeedsReview" => "À classer",
        "Published" => "Publié",
        "Rejected" => "Refusé",
        "Unpublished" => "Dépublié",
        "Merged" => "Fusionné",
        _ => status,
    };

    public static string Job(string state) => state switch
    {
        "Pending" => "En attente",
        "Running" => "En cours",
        "Succeeded" => "Terminé",
        "Failed" => "Échec",
        _ => state,
    };

    public static string Step(string step) => step switch
    {
        "queued" => "en file",
        "sources" => "sources",
        "facts" => "faits",
        "story" => "rédaction",
        "done" => "terminé",
        _ => step,
    };

    public static string Kind(string kind) => kind switch
    {
        "Standard" => "Histoire (≈ 2 min)",
        "Anecdote" => "Anecdote",
        "OnboardingClip" => "Extrait d'accueil",
        _ => kind,
    };

    // ---- Creators (T-1202)

    public static string CreatorStatus(string status) => status switch
    {
        "draft" => "Brouillon",
        "published" => "Publié",
        "suspended" => "Suspendu",
        _ => status,
    };

    /// <summary>Why a creator cannot be published yet, in words for the editor. Null when nothing blocks.</summary>
    public static string? PublishBlock(string? code) => code switch
    {
        null => null,
        "terms_required" => "Publication impossible : les CGU créateurs ne sont pas acceptées et aucun consentement fondateur n'est enregistré.",
        "specialty_required" => "Publication impossible : choisissez au moins une spécialité.",
        _ => "Publication impossible : le profil n'est pas complet.",
    };

    public static string Consent(string? termsVersion) => termsVersion switch
    {
        null or "" => "Aucun consentement",
        "fondateur" => "Consentement fondateur",
        var version => $"CGU {version}",
    };

    public static string ModerationReason(string reason) => reason switch
    {
        "inaccurate" => "Inexact",
        "misleading" => "Trompeur",
        "undeclared_ad" => "Publicité non déclarée",
        "inappropriate" => "Contenu inapproprié",
        "impersonation" => "Usurpation",
        "other" => "Autre",
        _ => reason,
    };

    public static string ModerationDecision(string? decision) => decision switch
    {
        "upheld" => "Signalement retenu : élément retiré",
        "dismissed" => "Classé sans suite",
        _ => string.Empty,
    };

    public static string LinkStatus(string status) => status switch
    {
        "proposed" => "Proposée",
        "validated" => "Validée",
        "rejected" => "Rejetée",
        _ => status,
    };

    public static string ContentStatus(string status) => status switch
    {
        "imported" => "En ligne",
        "hidden" => "Masqué",
        "removed" => "Retiré",
        _ => status,
    };

    public static string Platform(string platform) => platform switch
    {
        "youtube" => "YouTube",
        "instagram" => "Instagram",
        "tiktok" => "TikTok",
        _ => platform,
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
