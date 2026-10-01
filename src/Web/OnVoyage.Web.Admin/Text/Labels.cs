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
}
