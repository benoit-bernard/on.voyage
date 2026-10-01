using System.Text.Json;

namespace OnVoyage.Web.Public.Seo;

public sealed record Article(string Path, string Section, string Title, string PageTitle, string Description, string Kicker, string Meta, string Body);

/// <summary>
/// The former "micro-aventure" blog (D-15), imported by <c>tools/import-legacy-blog.py</c> into <c>Content/micro-aventures/*.json</c>.
/// Articles keep their old section and name, under <c>/micro-aventures</c>; every old address redirects to its new one.
/// </summary>
public sealed class ContentStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Old address → new address (301). The listing pages of the old site map to the sections of the new one.</summary>
    public static IReadOnlyDictionary<string, string> LegacyRedirects { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["/index.html"] = "/micro-aventures",
        ["/sur-leau.html"] = "/micro-aventures/eau",
        ["/sur-terre.html"] = "/micro-aventures/terre",
        ["/blog/equipement-micro-aventure.html"] = "/micro-aventures/blog/equipement-micro-aventure",
        ["/blog/silence-arctique.html"] = "/micro-aventures/blog/silence-arctique",
        ["/eau/allier-bivouac.html"] = "/micro-aventures/eau/allier-bivouac",
        ["/eau/descente-ardeche.html"] = "/micro-aventures/eau/descente-ardeche",
        ["/eau/dordogne-famille.html"] = "/micro-aventures/eau/dordogne-famille",
        ["/eau/gorges-verdon.html"] = "/micro-aventures/eau/gorges-verdon",
    };

    public ContentStore(IHostEnvironment environment)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Content", "micro-aventures");
        All = Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal)
                .Select(file => JsonSerializer.Deserialize<Article>(File.ReadAllText(file), Options)!)]
            : [];
    }

    public IReadOnlyList<Article> All { get; }

    public Article? Find(string path) => All.FirstOrDefault(article => string.Equals(article.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>The old "Sur l'eau" and "Sur terre" pages: <c>eau</c> holds the water articles, <c>terre</c> the rest.</summary>
    public IReadOnlyList<Article> InSection(string section) => section switch
    {
        "eau" => [.. All.Where(a => a.Section == "eau")],
        "terre" => [.. All.Where(a => a.Section != "eau")],
        _ => [],
    };
}
