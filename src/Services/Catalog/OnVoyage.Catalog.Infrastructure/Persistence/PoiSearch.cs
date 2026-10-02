using Microsoft.EntityFrameworkCore;

namespace OnVoyage.Catalog.Infrastructure.Persistence;

/// <summary>
/// The search query of F-14 (PostgreSQL): the name is compared without accents or case (<c>unaccent</c>), by substring and by trigram word
/// similarity (<c>pg_trgm</c>, which forgives a typo), and the short description and keywords by full text (<c>tsvector</c>, french configuration).
/// A place matches when any of the three does; the order puts a name that starts with the text first, then one that contains it, then the best
/// similarity, with a small bonus for a full-text hit. Written as a single translatable LINQ query so that EF Core builds the parameters: the typed
/// text is never concatenated into SQL.
/// </summary>
internal static class PoiSearch
{
    /// <summary>Below this word similarity a name is not a typo of the text but another word.</summary>
    public const double MinSimilarity = 0.4;

    public static IQueryable<Guid> Build(CatalogDbContext db, string destinationSlug, string text, string lang)
    {
        var term = text.Trim();
        var contains = $"%{EscapeLike(term)}%";
        var prefix = $"{EscapeLike(term)}%";

        return
            from poiText in db.PoiTexts.AsNoTracking()
            join poi in db.Pois.AsNoTracking() on poiText.PoiId equals poi.Id
            where poiText.Lang == lang && poi.PublishedAt != null && poi.Destination.Slug == destinationSlug
            let plainName = EF.Functions.Unaccent(poiText.Name)
            let plainTerm = EF.Functions.Unaccent(term)
            let inName = EF.Functions.ILike(plainName, EF.Functions.Unaccent(contains), "\\")
            let startsName = EF.Functions.ILike(plainName, EF.Functions.Unaccent(prefix), "\\")
            let similarity = EF.Functions.TrigramsWordSimilarity(plainTerm, plainName)
            let inText = poiText.SearchVector.Matches(EF.Functions.PlainToTsQuery("french", term))
            where inName || similarity >= MinSimilarity || inText
            orderby (startsName ? 3d : 0d) + (inName ? 2d : 0d) + similarity + (inText ? 0.5d : 0d) descending, poiText.Name, poi.Id
            select poi.Id;
    }

    /// <summary>The characters that mean something to <c>LIKE</c> are escaped with a backslash: "100%" is a text, not a pattern.</summary>
    internal static string EscapeLike(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
