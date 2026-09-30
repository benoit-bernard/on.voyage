using OnVoyage.Catalog.Domain;

namespace OnVoyage.Catalog.Infrastructure.Seed;

/// <summary>
/// Hand-written MVP-0 seed for Marseille. It stands in for the Factory pipeline (T-201..T-306) until that exists:
/// texts are original, unverified by the editorial workflow and flagged as not AI-generated.
/// </summary>
internal static class MarseilleSeed
{
    public static readonly Destination Marseille = new("marseille", "Marseille", new GeoPoint(43.2965, 5.3698));

    private static Guid Id(int n) => new($"0192a000-0000-7000-8000-{n:D12}");

    private static Poi Place(int n, string slug, string name, string category, double lat, double lon, double importance, double quality,
        int crowd, bool gem, (string Code, double Weight)[] weights, string title, string text) =>
        new(Id(n), slug, name, category, new GeoPoint(lat, lon), importance, quality, crowd, gem,
            WithLevelOne(weights),
            [new Story(Id(1000 + n), "fr", title, text, EstimateSeconds(text), null, false)]);

    /// <summary>Level-1 weight of a place is the maximum of its children (§6.1).</summary>
    private static Dictionary<string, double> WithLevelOne((string Code, double Weight)[] weights)
    {
        var result = weights.ToDictionary(w => w.Code, w => w.Weight);
        foreach (var group in weights.GroupBy(w => OnVoyage.Taxonomy.Interests.LevelOneOf(w.Code)))
        {
            result[group.Key] = Math.Max(result.GetValueOrDefault(group.Key), group.Max(w => w.Weight));
        }

        return result;
    }

    private static int EstimateSeconds(string text) => (int)Math.Round(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 150d * 60d);

    public static readonly IReadOnlyList<Poi> Pois =
    [
        Place(1, "notre-dame-de-la-garde", "Notre-Dame de la Garde", "religion", 43.2840, 5.3713, 0.98, 0.9, 5, false,
            [("religion.churches", 1), ("architecture.religious", 0.7), ("nature.viewpoints", 0.8), ("history.local", 0.5)],
            "La Bonne Mère",
            "Posée sur un calcaire de cent cinquante mètres, la basilique veille sur la ville depuis le dix-neuvième siècle. Sa statue dorée de la Vierge domine le clocher, et les marins y accrochent depuis toujours des ex-voto, maquettes de bateaux et tableaux de naufrages évités. Montez jusqu'à la terrasse : d'ici, toute la rade s'offre à vous, des îles du Frioul aux toits du Panier. C'est le meilleur endroit pour comprendre comment Marseille s'est construite entre la mer et la colline."),
        Place(2, "vieux-port", "Vieux-Port", "history", 43.2951, 5.3740, 0.95, 0.85, 5, false,
            [("history.maritime", 1), ("history.antiquity", 0.6), ("gastronomy.markets", 0.4), ("history.local", 0.6)],
            "Là où tout a commencé",
            "Des marins grecs venus de Phocée ont accosté dans cette crique il y a plus de deux mille six cents ans. Le port est resté le cœur de la ville, avec son marché aux poissons tous les matins et son ombrière de miroir qui reflète les passants. Levez les yeux vers les deux forts qui gardent l'entrée : ils racontent des siècles de commerce, de quarantaines et de surveillance."),
        Place(3, "fort-saint-jean", "Fort Saint-Jean", "architecture", 43.2960, 5.3618, 0.8, 0.85, 4, false,
            [("architecture.defensive", 1), ("history.military", 0.9), ("history.maritime", 0.6), ("history.middle_ages", 0.4)],
            "La sentinelle de l'entrée du port",
            "Fondé par les chevaliers de l'ordre de Saint-Jean de Jérusalem, le fort est ensuite remanié par Louis le quatorzième, qui y voit autant une protection qu'un moyen de surveiller une ville réputée rebelle. Aujourd'hui relié au musée des civilisations de l'Europe et de la Méditerranée par une passerelle, il offre des jardins suspendus et un regard direct sur la mer."),
        Place(4, "mucem", "MuCEM", "culture", 43.2967, 5.3609, 0.88, 0.9, 4, false,
            [("culture.museums", 1), ("architecture.modern", 0.9), ("culture.traditions", 0.6)],
            "Une dentelle de béton",
            "Ouvert en deux mille treize, le bâtiment carré de Rudy Ricciotti est enveloppé d'une résille de béton fibré qui filtre la lumière comme une dentelle. Le musée raconte les civilisations de la Méditerranée, mais la vraie surprise est la passerelle qui rejoint le fort voisin, suspendue au-dessus des vagues."),
        Place(5, "abbaye-saint-victor", "Abbaye Saint-Victor", "religion", 43.2899, 5.3660, 0.7, 0.85, 2, true,
            [("religion.abbeys", 1), ("history.middle_ages", 0.8), ("architecture.romanesque", 0.7), ("history.antiquity", 0.5)],
            "Une forteresse de prière",
            "Loin de la foule du Vieux-Port, cette abbaye fortifiée garde une crypte où reposent des sarcophages des premiers siècles chrétiens. Ses murs épais et ses tours crénelées rappellent qu'au Moyen Âge les moines devaient aussi se défendre des pirates. Descendez dans la crypte : le silence y est saisissant, et l'air plus frais qu'au dehors."),
        Place(6, "le-panier", "Le Panier", "villages", 43.2993, 5.3693, 0.85, 0.8, 4, false,
            [("villages.remarkable", 0.8), ("history.local", 0.9), ("culture.street_art", 0.7), ("culture.crafts", 0.5)],
            "Le plus vieux quartier",
            "C'est ici que s'élevait la ville grecque, sur la butte qui domine le port. Les ruelles en pente, le linge aux fenêtres, les ateliers d'artisans et les fresques murales composent un village dans la ville. Perdez-vous volontairement, puis redescendez par les escaliers : chaque détour réserve une placette ou un mur peint."),
        Place(7, "vieille-charite", "La Vieille Charité", "culture", 43.3006, 5.3678, 0.72, 0.9, 2, true,
            [("architecture.baroque", 0.9), ("culture.museums", 0.8), ("history.early_modern", 0.7), ("architecture.classical", 0.5)],
            "Un hospice devenu musée",
            "Construit au dix-septième siècle pour enfermer les pauvres de la ville, ce bel ensemble d'arcades de pierre rose entoure une chapelle à coupole ovale. Sauvé de la démolition au vingtième siècle, il abrite aujourd'hui des musées et des expositions. Prenez le temps de faire le tour de la cour : la lumière sur les arcades change toute la journée."),
        Place(8, "palais-longchamp", "Palais Longchamp", "architecture", 43.3046, 5.3947, 0.75, 0.85, 2, true,
            [("architecture.19th_century", 0.9), ("curiosities.engineering", 0.7), ("leisure.parks_gardens", 0.7), ("culture.museums", 0.5)],
            "La fête de l'eau",
            "Pour célébrer l'arrivée de l'eau de la Durance en ville, au dix-neuvième siècle, on a bâti ce monument en forme de château d'eau triomphal, avec cascades, colonnades et taureaux de bronze. L'eau ici n'est pas un décor : c'est la raison pour laquelle Marseille a pu grandir. Le parc alentour est un bon endroit pour une pause."),
        Place(9, "chateau-d-if", "Château d'If", "history", 43.2799, 5.3256, 0.85, 0.85, 4, false,
            [("history.military", 0.8), ("culture.literature", 0.9), ("history.early_modern", 0.6), ("architecture.defensive", 0.7)],
            "La prison du comte de Monte-Cristo",
            "Forteresse du seizième siècle construite sur un îlot, le château d'If a surtout servi de prison d'État. Alexandre Dumas y a enfermé son héros, Edmond Dantès, et des visiteurs du monde entier cherchent encore sa cellule. Traversée en bateau depuis le Vieux-Port : l'approche de l'îlot fait déjà partie du voyage."),
        Place(10, "cite-radieuse", "La Cité radieuse", "architecture", 43.2618, 5.3960, 0.78, 0.9, 2, true,
            [("architecture.modern", 1), ("culture.contemporary_art", 0.5), ("curiosities.engineering", 0.5)],
            "Une ville dans un immeuble",
            "Conçue par Le Corbusier et achevée au début des années cinquante, cette « unité d'habitation » abrite des appartements, des commerces et un toit-terrasse en un seul bloc de béton sur pilotis. C'était l'utopie d'une vie collective moderne. Le toit offre des vues sur les collines et la mer, et le bâtiment reste habité aujourd'hui."),
        Place(11, "calanque-de-sormiou", "Calanque de Sormiou", "nature", 43.2145, 5.4225, 0.82, 0.85, 3, false,
            [("nature.coast", 1), ("nature.cliffs_gorges", 0.9), ("outdoors.hiking", 0.7), ("leisure.beaches", 0.6)],
            "Le fjord blanc",
            "Entre falaises de calcaire blanc et eau turquoise, Sormiou est l'une des plus grandes calanques. L'accès est réglementé en été à cause du risque d'incendie : renseignez-vous avant de partir. Emportez de l'eau, des chaussures fermées, et respectez les sentiers balisés, car la végétation et les nichées d'oiseaux sont fragiles."),
        Place(12, "vallon-des-auffes", "Vallon des Auffes", "villages", 43.2880, 5.3477, 0.68, 0.85, 2, true,
            [("villages.fishing", 1), ("gastronomy.local_cuisine", 0.7), ("nature.coast", 0.6), ("history.maritime", 0.5)],
            "Le village dans la ville",
            "Niché sous le pont de la Corniche, ce petit port de pêche aux barques colorées donne l'impression d'être dans un village isolé alors que le centre n'est qu'à quelques minutes. Les pêcheurs y réparent encore leurs filets. Essayez d'y venir tôt le matin ou en fin de journée, quand la lumière est douce et le lieu presque vide."),
        Place(13, "cathedrale-de-la-major", "Cathédrale de la Major", "religion", 43.3003, 5.3645, 0.7, 0.8, 3, false,
            [("religion.churches", 1), ("architecture.19th_century", 0.8), ("architecture.religious", 0.8)],
            "Un géant byzantin",
            "À deux pas du nouveau port, cette immense cathédrale du dix-neuvième siècle mêle influences romanes et byzantines, avec des bandes de pierre blanche et verte et un intérieur étonnamment grand. Elle a été bâtie à côté de l'ancienne cathédrale, dont il reste quelques vestiges médiévaux à visiter."),
        Place(14, "parc-borely", "Parc Borély", "leisure", 43.2594, 5.3825, 0.55, 0.8, 1, true,
            [("leisure.parks_gardens", 1), ("leisure.family", 0.7), ("nature.flora", 0.6), ("architecture.classical", 0.4)],
            "Le poumon du Prado",
            "Autour d'un château du dix-huitième siècle, ce parc mêle jardin à la française, roseraie et grande pelouse. Il est moins connu des visiteurs que les sites du centre, et les familles marseillaises y viennent pour la promenade du dimanche. Une très bonne halte en se rendant vers les plages."),
        Place(15, "friche-belle-de-mai", "La Friche la Belle de Mai", "culture", 43.3114, 5.3922, 0.6, 0.8, 1, true,
            [("culture.contemporary_art", 0.9), ("culture.music", 0.6), ("history.industrial", 0.7), ("culture.street_art", 0.6)],
            "Une manufacture de tabac devenue fabrique de culture",
            "Cette ancienne manufacture de tabac accueille aujourd'hui des ateliers d'artistes, des salles de concert, des expositions et un toit-terrasse. Le grand bâtiment industriel a gardé ses volumes. Le week-end, on y vient pour le marché, les concerts et la vue sur la ville depuis le toit."),
    ];
}
