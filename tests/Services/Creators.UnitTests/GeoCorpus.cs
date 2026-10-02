using System.Security.Cryptography;
using System.Text;
using OnVoyage.Creators.Application.Features;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Domain;
using OnVoyage.Creators.Infrastructure.GeoAssociation;

namespace Creators.UnitTests;

/// <summary>A small directory of places and fifty captions annotated by hand (T-1209): what a creator wrote, and which places it is really about.</summary>
internal static class GeoCorpus
{
    public static Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes(name))[..16]);

    public static readonly Guid Marseille = Id("dest:marseille");
    public static readonly Guid Provence = Id("dest:provence");
    public static readonly Guid Paris = Id("dest:paris");

    private static PoiEntry Poi(string name, Guid destination, string slug, string? city = null, int importance = 60, string? english = null, params string[] aliases) =>
        new(Id(name), destination, slug, name, english, aliases, city ?? slug, importance, true, 1);

    public static IReadOnlyList<PoiEntry> Directory { get; } =
    [
        Poi("Vieux-Port", Marseille, "marseille", "Marseille", 90, "Old Port"),
        Poi("Notre-Dame de la Garde", Marseille, "marseille", "Marseille", 95, null, "Bonne Mère"),
        Poi("Fort Saint-Jean", Marseille, "marseille", "Marseille", 70),
        Poi("MuCEM", Marseille, "marseille", "Marseille", 80),
        Poi("Château d'If", Marseille, "marseille", "Marseille", 85),
        Poi("Calanque de Sormiou", Marseille, "marseille", "Marseille", 75),
        Poi("Calanque de Sugiton", Marseille, "marseille", "Marseille", 70),
        Poi("Le Panier", Marseille, "marseille", "Marseille", 75),
        Poi("Palais Longchamp", Marseille, "marseille", "Marseille", 65),
        Poi("Vallon des Auffes", Marseille, "marseille", "Marseille", 60),
        Poi("Corniche Kennedy", Marseille, "marseille", "Marseille", 70),
        Poi("Plage des Catalans", Marseille, "marseille", "Marseille", 55),
        Poi("Cathédrale de la Major", Marseille, "marseille", "Marseille", 65),
        Poi("Basilique Saint-Victor", Marseille, "marseille", "Marseille", 55),
        Poi("Île du Frioul", Marseille, "marseille", "Marseille", 60, null, "Frioul"),
        Poi("Parc Borély", Marseille, "marseille", "Marseille", 50),
        Poi("Cours Julien", Marseille, "marseille", "Marseille", 55),
        Poi("Cassis", Provence, "provence", "Cassis", 85),
        Poi("Gordes", Provence, "provence", "Gordes", 85),
        Poi("Roussillon", Provence, "provence", "Roussillon", 75),
        Poi("Lourmarin", Provence, "provence", "Lourmarin", 65),
        Poi("Aix-en-Provence", Provence, "provence", "Aix-en-Provence", 90),
        Poi("Cours Mirabeau", Provence, "provence", "Aix-en-Provence", 70),
        Poi("Abbaye de Sénanque", Provence, "provence", "Gordes", 70),
        Poi("Pont du Gard", Provence, "provence", "Vers-Pont-du-Gard", 85),
        Poi("Arles", Provence, "provence", "Arles", 85),
        Poi("Les Baux-de-Provence", Provence, "provence", "Les Baux-de-Provence", 75),
        Poi("Mont Ventoux", Provence, "provence", "Bédoin", 80),
        Poi("Avignon", Provence, "provence", "Avignon", 90),
        Poi("Palais des Papes", Provence, "provence", "Avignon", 85),
        Poi("Notre-Dame de Paris", Paris, "paris", "Paris", 100),
        Poi("Tour Eiffel", Paris, "paris", "Paris", 100, "Eiffel Tower"),
        Poi("Montmartre", Paris, "paris", "Paris", 85),
    ];

    /// <summary>What the database does with a mention (substring or trigram match on the names), without the database.</summary>
    public static IReadOnlyList<PoiEntry> Candidates(string normalizedName) =>
        [.. Directory.Where(poi => PlaceMatcher.NameScore(normalizedName, poi).Score >= PlaceMatcher.MinNameScore).Take(12)];

    /// <summary>The chain of the handler without its plumbing: read, merge chapters, find candidates, match.</summary>
    public static async Task<IReadOnlyList<PlaceMatch>> RunAsync(string title, string? caption, IReadOnlyList<Chapter>? chapters = null, params Guid[] creatorDestinations)
    {
        var input = new GeotagInput(title, caption, chapters ?? [], "fr");
        var read = await new OfflinePlaceMentionExtractor().ExtractAsync(input, CancellationToken.None);
        var mentions = GeoAssociationHandler.MergeChapters(read, input.Chapters);
        var items = mentions.Select(mention => (mention, Candidates(PlaceMatcher.Normalize(mention.Name)))).ToList();
        return PlaceMatcher.Match(items, new MatchContext(new HashSet<Guid>(creatorDestinations)));
    }

    /// <summary>(caption, the places it is really about). The creator is from Marseille in every case.</summary>
    public static IReadOnlyList<(string Caption, string[] Expected)> Captions { get; } =
    [
        ("Coucher de soleil sur le Vieux-Port, vue depuis le Fort Saint-Jean #marseille", ["Vieux-Port", "Fort Saint-Jean"]),
        ("Montée à Notre-Dame de la Garde au lever du jour", ["Notre-Dame de la Garde"]),
        ("Journée au MuCEM puis balade dans le Panier", ["MuCEM", "Le Panier"]),
        ("Bateau pour le Château d'If depuis le Vieux-Port", ["Château d'If", "Vieux-Port"]),
        ("Randonnée dans la Calanque de Sormiou, eau turquoise", ["Calanque de Sormiou"]),
        ("Baignade à la Calanque de Sugiton, attention à l'affluence", ["Calanque de Sugiton"]),
        ("Dimanche à Cassis, le port et le cap Canaille", ["Cassis"]),
        ("Les ocres de Roussillon sont incroyables", ["Roussillon"]),
        ("Gordes et l'abbaye de Sénanque en lavande", ["Gordes", "Abbaye de Sénanque"]),
        ("Marché de Lourmarin le vendredi matin", ["Lourmarin"]),
        ("Cours Mirabeau à Aix-en-Provence sous les platanes", ["Cours Mirabeau", "Aix-en-Provence"]),
        ("Pont du Gard en canoë, un classique", ["Pont du Gard"]),
        ("Arles sur les pas de Van Gogh", ["Arles"]),
        ("Les Baux-de-Provence au crépuscule", ["Les Baux-de-Provence"]),
        ("Ascension du Mont Ventoux à vélo", ["Mont Ventoux"]),
        ("Avignon, le Palais des Papes et le pont", ["Avignon", "Palais des Papes"]),
        ("Le Palais Longchamp et son château d'eau", ["Palais Longchamp"]),
        ("Dîner au Vallon des Auffes face à la mer", ["Vallon des Auffes"]),
        ("Vélo sur la Corniche Kennedy jusqu'aux Catalans", ["Corniche Kennedy", "Plage des Catalans"]),
        ("Plage des Catalans un dimanche d'été", ["Plage des Catalans"]),
        ("La Major, cathédrale oubliée de Marseille", ["Cathédrale de la Major"]),
        ("Messe à la Basilique Saint-Victor", ["Basilique Saint-Victor"]),
        ("Journée au Frioul, île sauvage", ["Île du Frioul"]),
        ("Pique-nique au Parc Borély", ["Parc Borély"]),
        ("Shopping au Cours Julien", ["Cours Julien"]),
        ("Notre-Dame ce matin", ["Notre-Dame de la Garde"]),
        ("La Bonne Mère veille sur la ville", ["Notre-Dame de la Garde"]),
        ("Montmartre et la Tour Eiffel, 3 jours à Paris", ["Montmartre", "Tour Eiffel"]),
        ("Notre-Dame de Paris renaît", ["Notre-Dame de Paris"]),
        ("Weekend à Aix-en-Provence", ["Aix-en-Provence"]),
        ("Merci à Marie pour l'invitation ! Abonnez-vous à ma chaîne YouTube", []),
        ("Nouvelle vidéo sur Instagram et TikTok, lien en bio", []),
        ("Mon sac à dos Quechua pour la randonnée", []),
        ("Bonjour à tous ! Aujourd'hui on part à Gordes", ["Gordes"]),
        ("Gordes Roussillon Lourmarin : le Luberon en 3 jours", ["Gordes", "Roussillon", "Lourmarin"]),
        ("Visite guidée du Palais des Papes à Avignon avec Julien", ["Palais des Papes", "Avignon"]),
        ("Rendez-vous le 14 juillet à Cassis", ["Cassis"]),
        ("Tour de la Corniche Kennedy en trottinette", ["Corniche Kennedy"]),
        ("Balade au Vieux Port de Marseille", ["Vieux-Port"]),
        ("Les calanques de Marseille en bateau", []),
        ("Cap sur l'île du Frioul", ["Île du Frioul"]),
        ("Boulangerie Paul à la gare, puis direction Cassis", ["Cassis"]),
        ("Aix-en-Provence ou Avignon ? Mon avis", ["Aix-en-Provence", "Avignon"]),
        ("Le Château d'If et ses cachots", ["Château d'If"]),
        ("Randonnée vers Sormiou puis Sugiton", ["Calanque de Sormiou", "Calanque de Sugiton"]),
        ("Pont du Gard, Arles et Avignon : roadtrip romain", ["Pont du Gard", "Arles", "Avignon"]),
        ("Vue sur Marseille depuis Notre-Dame de la Garde ☀️", ["Notre-Dame de la Garde"]),
        ("Promenade au Parc Borély puis plage", ["Parc Borély"]),
        ("Le Mont Ventoux vu de Bédoin", ["Mont Ventoux"]),
        ("Cassis, Gordes et Roussillon : mes 3 villages préférés", ["Cassis", "Gordes", "Roussillon"]),
    ];
}
