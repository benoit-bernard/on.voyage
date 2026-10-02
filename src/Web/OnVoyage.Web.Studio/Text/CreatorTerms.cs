namespace OnVoyage.Web.Studio.Text;

/// <summary>
/// The commitments of the creator terms (CGU créateurs) as the sign-up page lists them (F-26). This is a summary written from the specification:
/// the legal text itself is H-010 (a lawyer's work) and replaces it before launch; the server holds the version that is accepted.
/// </summary>
public static class CreatorTerms
{
    public const string Notice = "Texte provisoire, en attente de la rédaction juridique des CGU créateurs.";

    public static IReadOnlyList<string> Commitments { get; } =
    [
        "Vous autorisez ON.VOYAGE à afficher votre nom, votre photo, votre bio, vos conseils et les vignettes de vos contenus sur votre page et sur les pages des lieux. Vos vidéos et photos restent chez vous : ON.VOYAGE n'en garde que la référence et le lien.",
        "Vous déclarez toute collaboration commerciale : un contenu lié à une rémunération porte l'étiquette « Publicité ». Une omission confirmée entraîne le masquage du contenu et un avertissement.",
        "Vous respectez les sites fragiles et les consignes de respect affichées avec les lieux, et vous ne poussez pas la sur-fréquentation d'un lieu sensible.",
        "Vous êtes responsable de ce que vous publiez. ON.VOYAGE modère : un signalement fondé peut retirer un élément, avec un exposé des motifs et la possibilité de contester la décision.",
        "Vos abonnés ne vous sont jamais communiqués : vous ne voyez que des statistiques agrégées, sous un seuil de 20.",
        "Vous pouvez quitter ON.VOYAGE à tout moment : supprimer votre compte supprime votre page, vos contenus référencés et vos conseils.",
    ];
}
