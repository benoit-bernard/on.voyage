using System.Text.RegularExpressions;
using OnVoyage.Factory.Domain.Dedup;

namespace OnVoyage.Factory.Domain.Content;

public sealed record OverlapResult(int LongestSharedWords, double FiveGramJaccard, bool Passes);

/// <summary>
/// Anti-plagiarism of §8.6: the story must not repeat a source. Two measures per source document, both on normalised words: the
/// longest run of words they share, and the Jaccard overlap of their 5-word shingles. Named after its job, not "TextUtils" (§23.2).
/// </summary>
public static class VerbatimOverlapDetector
{
    public const int MaxSharedWords = 8;
    public const double MaxJaccard = 0.03;
    public const int ShingleSize = 5;

    public static OverlapResult Compare(string story, string source)
    {
        var a = Words(story);
        var b = Words(source);
        var longest = LongestCommonRun(a, b);
        var jaccard = Jaccard(Shingles(a), Shingles(b));
        return new OverlapResult(longest, jaccard, longest < MaxSharedWords && jaccard < MaxJaccard);
    }

    /// <summary>The worst result over every source document.</summary>
    public static OverlapResult Worst(string story, IEnumerable<string> sources)
    {
        var results = sources.Select(source => Compare(story, source)).ToList();
        return results.Count == 0
            ? new OverlapResult(0, 0, true)
            : new OverlapResult(results.Max(result => result.LongestSharedWords), results.Max(result => result.FiveGramJaccard), results.All(result => result.Passes));
    }

    internal static string[] Words(string text) => NameSimilarity.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static int LongestCommonRun(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var positions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var j = 0; j < b.Length; j++)
        {
            if (!positions.TryGetValue(b[j], out var list))
            {
                positions[b[j]] = list = [];
            }

            list.Add(j);
        }

        var best = 0;
        var previous = new Dictionary<int, int>();
        for (var i = 0; i < a.Length; i++)
        {
            var current = new Dictionary<int, int>();
            if (positions.TryGetValue(a[i], out var matches))
            {
                foreach (var j in matches)
                {
                    var length = previous.GetValueOrDefault(j - 1) + 1;
                    current[j] = length;
                    best = Math.Max(best, length);
                }
            }

            previous = current;
        }

        return best;
    }

    private static HashSet<string> Shingles(string[] words)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + ShingleSize <= words.Length; i++)
        {
            result.Add(string.Join(' ', words.Skip(i).Take(ShingleSize)));
        }

        return result;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0d;
        }

        var shared = a.Count(b.Contains);
        return shared / (double)(a.Count + b.Count - shared);
    }
}

public sealed record CheckIssue(string Check, string Detail, string? Sentence = null);

public static partial class LengthCheck
{
    public const double Tolerance = 0.15;

    [GeneratedRegex("\\p{L}[\\p{L}\\p{N}'’-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    public static int CountWords(string text) => Word().Count(text);

    /// <summary>Word count within the kind's range, with the tolerance of §8.6 on both ends.</summary>
    public static CheckIssue? Check(string text, StoryKind kind)
    {
        var target = LengthTarget.For(kind);
        var words = CountWords(text);
        var min = (int)Math.Floor(target.MinWords * (1 - Tolerance));
        var max = (int)Math.Ceiling(target.MaxWords * (1 + Tolerance));
        return words < min || words > max
            ? new CheckIssue("length", $"{words} words, expected {target.MinWords}-{target.MaxWords} (±15 %).")
            : null;
    }
}

public static partial class StyleCheck
{
    public const int MaxSentenceWords = 30;

    /// <summary>Superlatives and brochure phrases that need a source the story cannot cite (§8.5).</summary>
    public static IReadOnlyList<string> ForbiddenTerms { get; } =
    [
        "incontournable", "à ne pas manquer", "magnifique", "splendide", "époustouflant", "somptueux", "unique au monde",
        "le plus beau", "la plus belle", "le plus grand", "la plus grande", "le plus célèbre", "la plus célèbre", "must-see", "breathtaking",
    ];

    [GeneratedRegex("(?m)^\\s*([-*•]|\\d+[.)])\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Bullet();

    [GeneratedRegex("(?<=[.!?…])\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBreak();

    public static IReadOnlyList<string> Sentences(string text) =>
        [.. SentenceBreak().Split(text.Trim()).Select(sentence => sentence.Trim()).Where(sentence => sentence.Length > 0)];

    public static IReadOnlyList<CheckIssue> Check(string text)
    {
        var issues = new List<CheckIssue>();
        if (Bullet().IsMatch(text))
        {
            issues.Add(new CheckIssue("style", "Lists and bullets do not read well aloud."));
        }

        foreach (var sentence in Sentences(text))
        {
            if (LengthCheck.CountWords(sentence) > MaxSentenceWords)
            {
                issues.Add(new CheckIssue("style", $"Sentence longer than {MaxSentenceWords} words.", sentence));
            }
        }

        var lowered = text.ToLowerInvariant();
        foreach (var term in ForbiddenTerms.Where(term => lowered.Contains(term, StringComparison.Ordinal)))
        {
            issues.Add(new CheckIssue("style", $"Unsourced superlative or brochure phrase: \"{term}\"."));
        }

        return issues;
    }
}

/// <summary>Content that must never be published without a person reading it (§8.6 "Sécurité"): medical, partisan, dangerous advice.</summary>
public static class SafetyCheck
{
    private static readonly string[] RiskyPhrases =
    [
        "baignez-vous", "baignade conseillée", "plongez depuis", "sautez depuis", "hors sentier", "quittez le sentier", "escaladez",
        "traitement", "médicament", "guérit", "soigne", "diagnostic", "votez", "électeurs", "gouvernement a eu tort", "parti politique",
    ];

    public static IReadOnlyList<CheckIssue> Check(string text)
    {
        var lowered = text.ToLowerInvariant();
        return [.. RiskyPhrases.Where(phrase => lowered.Contains(phrase, StringComparison.Ordinal)).Select(phrase => new CheckIssue("safety", $"Sensitive wording: \"{phrase}\"."))];
    }
}

/// <summary>Quality score of §8.9.</summary>
public static class QualityScore
{
    public const double WarningThreshold = 0.80;

    public static double Compute(double sourceQuality, double factualConfidence, double textQuality, double audioQuality, double editorialScore) =>
        Math.Round((0.30 * sourceQuality) + (0.30 * factualConfidence) + (0.20 * textQuality) + (0.10 * audioQuality) + (0.10 * editorialScore), 4);

    /// <summary><c>mean(fact confidence) × (1 − max(0, genericShare − 0.3))</c>.</summary>
    public static double FactualConfidence(IReadOnlyCollection<double> factConfidences, double genericShare) =>
        factConfidences.Count == 0 ? 0d : factConfidences.Average() * (1d - Math.Max(0d, genericShare - 0.3));

    public static double TextQuality(int styleIssues) => Math.Max(0d, 1d - (0.1 * styleIssues));
}

/// <summary>Per-destination substitutions applied before synthesis (§8.7): Provençal names, acronyms.</summary>
public static class PronunciationLexicon
{
    public static string Apply(string text, IReadOnlyDictionary<string, string> lexicon)
    {
        foreach (var (term, replacement) in lexicon.OrderByDescending(pair => pair.Key.Length))
        {
            text = Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}])", replacement, RegexOptions.CultureInvariant);
        }

        return text;
    }
}
