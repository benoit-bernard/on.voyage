namespace OnVoyage.Taxonomy;

/// <summary>Taxonomy v1 (cahier des charges, annexe D): 10 level-1 nodes and 64 level-2 nodes = 74 dimensions.</summary>
public static class Interests
{
    public const int Version = 1;

    public static IReadOnlyDictionary<string, string[]> Tree { get; } = new Dictionary<string, string[]>
    {
        ["history"] = ["antiquity", "middle_ages", "renaissance", "early_modern", "revolution_empire", "19th_century", "world_wars", "military", "maritime", "industrial", "local"],
        ["architecture"] = ["romanesque", "gothic", "classical", "baroque", "19th_century", "modern", "defensive", "religious", "vernacular", "industrial"],
        ["nature"] = ["coast", "cliffs_gorges", "mountain", "forest", "wetlands", "geology", "caves", "rivers_waterfalls", "flora", "fauna", "viewpoints"],
        ["culture"] = ["museums", "painting", "contemporary_art", "literature", "cinema", "music", "crafts", "traditions", "street_art"],
        ["religion"] = ["churches", "abbeys", "pilgrimage"],
        ["villages"] = ["perched", "fishing", "remarkable"],
        ["gastronomy"] = ["local_cuisine", "wine", "markets", "producers", "olive_oil", "cheese"],
        ["curiosities"] = ["science", "astronomy", "legends", "engineering"],
        ["outdoors"] = ["hiking", "water_sports", "cycling", "bivouac"],
        ["leisure"] = ["beaches", "parks_gardens", "family"],
    };

    public static IReadOnlyList<string> LevelOne { get; } = [.. Tree.Keys];

    public static IReadOnlyList<string> All { get; } =
        [.. Tree.SelectMany(node => new[] { node.Key }.Concat(node.Value.Select(child => $"{node.Key}.{child}")))];

    /// <summary>Returns the level-1 code of any taxonomy code (<c>history.military</c> gives <c>history</c>).</summary>
    public static string LevelOneOf(string code) => code.Contains('.', StringComparison.Ordinal) ? code[..code.IndexOf('.', StringComparison.Ordinal)] : code;
}
