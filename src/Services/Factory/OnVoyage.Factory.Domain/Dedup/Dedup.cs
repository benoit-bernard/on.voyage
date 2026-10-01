using System.Globalization;
using System.Text;
using OnVoyage.Factory.Domain.Geo;

namespace OnVoyage.Factory.Domain.Dedup;

/// <summary>Name comparison as PostgreSQL's pg_trgm does it, so a database-side check and this one agree.</summary>
public static class NameSimilarity
{
    /// <summary>Lower-case, accents removed, anything that is not a letter or digit becomes a space, runs of spaces collapsed.</summary>
    public static string Normalize(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Jaccard similarity of the trigram sets (words padded with two spaces before and one after), in [0, 1].</summary>
    public static double Trigram(string left, string right)
    {
        var a = Trigrams(Normalize(left));
        var b = Trigrams(Normalize(right));
        if (a.Count == 0 && b.Count == 0)
        {
            return 0d;
        }

        var shared = a.Intersect(b).Count();
        return shared / (double)(a.Count + b.Count - shared);
    }

    private static HashSet<string> Trigrams(string normalized)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var padded = $"  {word} ";
            for (var i = 0; i + 3 <= padded.Length; i++)
            {
                result.Add(padded.Substring(i, 3));
            }
        }

        return result;
    }
}

public sealed record DedupOptions(double MaxDistanceMeters = 75d, double ProposeSimilarity = 0.6, double AutoMergeSimilarity = 0.85);

/// <summary>What the detector knows about a place.</summary>
public sealed record DedupSubject(Guid Id, string Name, GeoPoint Location, string? Qid, Footprint? Footprint);

public enum DedupVerdict
{
    /// <summary>Unrelated places.</summary>
    Distinct,

    /// <summary>Probably the same place; a person confirms in the back office.</summary>
    Propose,

    /// <summary>The same place with certainty: merged without review. Every merge is logged and reversible.</summary>
    AutoMerge,
}

public sealed record DedupDecision(DedupVerdict Verdict, string Reason, double Similarity, double DistanceMeters);

/// <summary>Rules of §7.4.</summary>
public sealed class DuplicateDetector(DedupOptions? options = null)
{
    private readonly DedupOptions _options = options ?? new DedupOptions();

    public DedupDecision Compare(DedupSubject first, DedupSubject second)
    {
        var distance = first.Location.DistanceTo(second.Location);
        var similarity = NameSimilarity.Trigram(first.Name, second.Name);

        if (first.Qid is not null && second.Qid is not null)
        {
            // Two different Wikidata items are two different things, whatever their names and positions (a museum inside a fort).
            return string.Equals(first.Qid, second.Qid, StringComparison.OrdinalIgnoreCase)
                ? new DedupDecision(DedupVerdict.AutoMerge, "same_qid", similarity, distance)
                : new DedupDecision(DedupVerdict.Distinct, "different_qid", similarity, distance);
        }

        if (ContainedInOther(first, second))
        {
            return new DedupDecision(DedupVerdict.Distinct, "inside_footprint", similarity, distance);
        }

        if (distance >= _options.MaxDistanceMeters || similarity < _options.ProposeSimilarity)
        {
            return new DedupDecision(DedupVerdict.Distinct, "far_or_dissimilar", similarity, distance);
        }

        return similarity >= _options.AutoMergeSimilarity
            ? new DedupDecision(DedupVerdict.AutoMerge, "close_and_same_name", similarity, distance)
            : new DedupDecision(DedupVerdict.Propose, "close_and_similar_name", similarity, distance);
    }

    /// <summary>
    /// A point lying inside the area of another place that is not the same item stays distinct, unless their names are as good as
    /// identical (an OSM node duplicating the area it sits in).
    /// </summary>
    private bool ContainedInOther(DedupSubject first, DedupSubject second)
    {
        var inside = (second.Footprint?.Contains(first.Location) ?? false) || (first.Footprint?.Contains(second.Location) ?? false);
        return inside && NameSimilarity.Trigram(first.Name, second.Name) < _options.AutoMergeSimilarity;
    }
}
