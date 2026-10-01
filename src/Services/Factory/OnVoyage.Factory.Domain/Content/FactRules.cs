using System.Text.RegularExpressions;
using OnVoyage.Factory.Domain.Dedup;

namespace OnVoyage.Factory.Domain.Content;

public enum FactType
{
    Date,
    Person,
    Event,
    Architecture,
    Nature,
    Measure,
    Anecdote,
    Access,
}

public enum FactStatus
{
    Validated,
    Rejected,

    /// <summary>Contradicts a fact from another source: kept out of the writing until a person decides.</summary>
    Conflict,
}

public sealed record FactSnapshot(Guid Id, Guid DocumentId, FactType Type, string Statement);

/// <summary>A claim must be backed by an exact quote from its document (§8.4): this is the deterministic half of fact checking.</summary>
public static partial class QuoteValidator
{
    public const int MaxQuoteLength = 200;

    [GeneratedRegex("[\\u2018\\u2019\\u02BC\\u0060\\u00B4]", RegexOptions.CultureInvariant)]
    private static partial Regex Apostrophes();

    [GeneratedRegex("[\\u201C\\u201D\\u00AB\\u00BB]", RegexOptions.CultureInvariant)]
    private static partial Regex Quotes();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    /// <summary>Whitespace runs collapse to one space, typographic apostrophes and quotes become plain ones, no-break spaces become spaces.</summary>
    public static string Normalize(string text)
    {
        var plain = Apostrophes().Replace(text, "'");
        plain = Quotes().Replace(plain, "\"");
        plain = plain.Replace(' ', ' ').Replace(' ', ' ');
        return Spaces().Replace(plain, " ").Trim();
    }

    public static bool IsExactQuote(string quote, string document)
    {
        if (string.IsNullOrWhiteSpace(quote) || quote.Length > MaxQuoteLength)
        {
            return false;
        }

        return Normalize(document).Contains(Normalize(quote), StringComparison.Ordinal);
    }
}

/// <summary>Finds contradictions between two sources on the same kind of fact (§8.4): two different years for what looks like the same event.</summary>
public static partial class FactConflictDetector
{
    private const double SameSubjectSimilarity = 0.35;

    [GeneratedRegex("\\b(1[0-9]{3}|20[0-9]{2})\\b", RegexOptions.CultureInvariant)]
    private static partial Regex Years();

    [GeneratedRegex("\\b[0-9]+(?:[.,][0-9]+)?\\b", RegexOptions.CultureInvariant)]
    private static partial Regex Numbers();

    /// <summary>Ids of the facts that contradict a fact of another document. Both sides of a conflict are returned.</summary>
    public static IReadOnlySet<Guid> Detect(IReadOnlyList<FactSnapshot> facts)
    {
        var conflicting = new HashSet<Guid>();
        for (var i = 0; i < facts.Count; i++)
        {
            for (var j = i + 1; j < facts.Count; j++)
            {
                var (a, b) = (facts[i], facts[j]);
                if (a.DocumentId == b.DocumentId || a.Type != b.Type || a.Type is not (FactType.Date or FactType.Measure))
                {
                    continue;
                }

                if (NameSimilarity.Trigram(Strip(a.Statement), Strip(b.Statement)) < SameSubjectSimilarity)
                {
                    continue;
                }

                var left = Values(a);
                var right = Values(b);
                if (left.Count > 0 && right.Count > 0 && !left.Overlaps(right))
                {
                    conflicting.Add(a.Id);
                    conflicting.Add(b.Id);
                }
            }
        }

        return conflicting;
    }

    private static HashSet<string> Values(FactSnapshot fact) =>
        [.. (fact.Type == FactType.Date ? Years() : Numbers()).Matches(fact.Statement).Select(match => match.Value.Replace(',', '.'))];

    /// <summary>Compare the wording around the numbers, not the numbers themselves.</summary>
    private static string Strip(string statement) => Numbers().Replace(statement, " ");
}
