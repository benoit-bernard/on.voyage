using System.Text.RegularExpressions;

namespace OnVoyage.Creators.Domain;

/// <summary>
/// A place a creator's content talks about, as the assistant read it (F-28). <c>Confidence</c> is how sure the reader is that it is a real place
/// (not a brand, a person or a common word); <c>StartSeconds</c> is set when the mention is a chapter of a video; <c>Evidence</c> is the few words around it.
/// <c>ExactOnly</c> marks a piece of a longer phrase (« Gordes » in « Gordes Roussillon Lourmarin »): it only counts when it is a place's whole name.
/// </summary>
public sealed record PlaceMention(string Name, string? Type, string? City, string Evidence, double Confidence, int? StartSeconds = null, bool ExactOnly = false)
{
    public bool FromChapter => StartSeconds is not null;
}

/// <summary>What the matcher knows about the creator, to judge a candidate: the destinations they declared and those of the places they already validated.</summary>
public sealed record MatchContext(IReadOnlySet<Guid> CreatorDestinations);

public sealed record PlaceCandidateScore(PoiEntry Poi, double Confidence, IReadOnlyList<string> Signals);

/// <summary>
/// The answer for one mention: the best candidate with its confidence and signals, or none. <c>Alternatives</c> are the other plausible places,
/// best first, for the review screen.
/// </summary>
public sealed record PlaceMatch(PlaceMention Mention, PlaceCandidateScore? Best, IReadOnlyList<PlaceCandidateScore> Alternatives);

/// <summary>
/// Matches the mentions of a content to the directory of places (F-28): similarity of the name with every name and alias of a place (trigrams, as
/// <c>pg_trgm</c>), coherence of the city and of the destination, the creator's own destinations and the other places of the same content. The
/// result is a confidence from 0 to 1, deterministic. An ambiguous or partial name never reaches 0.9, the threshold of « Tout valider »: it goes to
/// manual review (« Notre-Dame » is not enough to know which one).
/// </summary>
public static partial class PlaceMatcher
{
    /// <summary>Candidates under this name score are not candidates at all.</summary>
    public const double MinNameScore = 0.45;

    /// <summary>A proposal below this confidence is not worth the creator's time.</summary>
    public const double MinProposal = 0.4;

    /// <summary>Two different places this close to each other make the best one ambiguous.</summary>
    public const double AmbiguityMargin = 0.12;

    public const double AmbiguousCap = 0.85;

    /// <summary>A partial name (« Notre-Dame » for « Notre-Dame de la Garde ») never goes above this.</summary>
    public const double PartialCap = 0.8;

    [GeneratedRegex(@"^(?:l'|d'|le |la |les |un |une |the |el |il )", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex LeadingArticle();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex NonWord();

    /// <summary>Lowercase, no accents, no article, single spaces: the form names are compared in.</summary>
    public static string Normalize(string? text)
    {
        var folded = PoiEntry.Fold(text).Replace('’', '\'');
        folded = LeadingArticle().Replace(folded.Trim(), string.Empty);
        return NonWord().Replace(folded, " ").Trim();
    }

    /// <summary>Trigram similarity as <c>pg_trgm</c> computes it: words padded with two spaces before and one after, |A∩B| / |A∪B|.</summary>
    public static double TrigramSimilarity(string left, string right)
    {
        var a = Trigrams(left);
        var b = Trigrams(right);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0d;
        }

        var shared = a.Count(b.Contains);
        return shared / (double)(a.Count + b.Count - shared);
    }

    private static HashSet<string> Trigrams(string text)
    {
        HashSet<string> grams = new(StringComparer.Ordinal);
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var padded = "  " + word + " ";
            for (var i = 0; i + 3 <= padded.Length; i++)
            {
                grams.Add(padded.Substring(i, 3));
            }
        }

        return grams;
    }

    /// <summary>How well a mention names a place, 0–1, and whether it is only part of the place's name.</summary>
    public static (double Score, bool Partial) NameScore(string mentionName, PoiEntry poi)
    {
        var mention = Normalize(mentionName);
        if (mention.Length == 0)
        {
            return (0d, false);
        }

        double best = 0d;
        var partial = false;
        foreach (var name in Names(poi))
        {
            double score;
            var isPartial = false;
            if (name == mention)
            {
                score = 1d;
            }
            else if (name.Contains(mention, StringComparison.Ordinal) && mention.Length >= 4)
            {
                // The mention is a piece of the name: « notre dame » in « notre dame de la garde ». The longer the piece, the likelier.
                score = 0.55 + (0.25 * mention.Length / name.Length);
                isPartial = true;
            }
            else if (mention.Contains(name, StringComparison.Ordinal) && name.Length >= 4)
            {
                // The mention adds words to the name: « fort saint jean de marseille ».
                score = 0.7 + (0.25 * name.Length / mention.Length);
            }
            else
            {
                score = TrigramSimilarity(mention, name);
            }

            if (score > best)
            {
                best = score;
                partial = isPartial;
            }
        }

        return (Math.Min(best, 1d), partial);
    }

    private static IEnumerable<string> Names(PoiEntry poi) =>
        new[] { poi.NameFr, poi.NameEn }.Concat(poi.Aliases).Where(name => !string.IsNullOrWhiteSpace(name)).Select(Normalize).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Matches every mention of one content. The second pass uses the first: places that sit in the destination most of the mentions point to get a
    /// boost, the others a malus (the same trip talks about the same region; there are no coordinates here, the destination stands for proximity).
    /// </summary>
    public static IReadOnlyList<PlaceMatch> Match(IReadOnlyList<(PlaceMention Mention, IReadOnlyList<PoiEntry> Candidates)> items, MatchContext context)
    {
        // Pass 1: names only, to find where the content is.
        List<Guid> anchors = [];
        foreach (var (mention, candidates) in items)
        {
            var ranked = Rank(mention, candidates, context, null);
            if (ranked.Count > 0 && ranked[0].Confidence >= 0.8 && ranked[0].Signals.Contains("exact"))
            {
                anchors.Add(ranked[0].Poi.DestinationId);
            }
        }

        Guid? dominant = null;
        if (anchors.Count >= 2)
        {
            var top = anchors.GroupBy(id => id).OrderByDescending(group => group.Count()).First();
            dominant = top.Count() * 2 > anchors.Count ? top.Key : null;
        }

        return [.. items.Select(item =>
        {
            var ranked = Rank(item.Mention, item.Candidates, context, dominant);
            return new PlaceMatch(item.Mention, ranked.FirstOrDefault(), ranked.Skip(1).Take(4).ToList());
        })];
    }

    private static List<PlaceCandidateScore> Rank(PlaceMention mention, IReadOnlyList<PoiEntry> candidates, MatchContext context, Guid? dominantDestination)
    {
        var scored = new List<(PoiEntry Poi, double Name, bool Partial, double Total, List<string> Signals)>();
        foreach (var poi in candidates.DistinctBy(candidate => candidate.PoiId))
        {
            var (name, partial) = NameScore(mention.Name, poi);
            if (name < MinNameScore || mention.ExactOnly && name < 0.999)
            {
                continue;
            }

            List<string> signals = ["text"];
            if (mention.FromChapter)
            {
                signals.Add("chapter");
            }

            if (name >= 0.999)
            {
                signals.Add("exact");
            }

            var total = name;
            if (!string.IsNullOrWhiteSpace(mention.City) && !string.IsNullOrWhiteSpace(poi.City))
            {
                if (Normalize(mention.City) == Normalize(poi.City))
                {
                    total += 0.05;
                    signals.Add("city");
                }
                else if (Normalize(poi.City).Contains(Normalize(mention.City), StringComparison.Ordinal) || Normalize(mention.City).Contains(Normalize(poi.City), StringComparison.Ordinal))
                {
                    total += 0.02;
                }
                else
                {
                    total *= 0.7;
                    signals.Add("city_mismatch");
                }
            }

            if (context.CreatorDestinations.Contains(poi.DestinationId))
            {
                total += 0.08;
                signals.Add("context");
            }

            if (dominantDestination is { } dominant)
            {
                total += poi.DestinationId == dominant ? 0.07 : -0.1;
            }

            if (mention.FromChapter)
            {
                total += 0.07; // a chapter is a deliberate title: the creator dedicated a segment of the video to it
            }

            total += poi.ImportanceScore / 100d * 0.03;
            if (!poi.IsPublished)
            {
                total -= 0.05;
            }

            scored.Add((poi, name, partial, total, signals));
        }

        var ordered = scored.OrderByDescending(item => item.Total).ThenByDescending(item => item.Poi.ImportanceScore).ThenBy(item => item.Poi.PoiId).ToList();
        var results = new List<PlaceCandidateScore>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            var confidence = Math.Clamp(item.Total, 0d, 1d);
            var signals = item.Signals;
            if (item.Partial)
            {
                confidence = Math.Min(confidence, PartialCap);
                signals.Add("partial");
            }

            // Ambiguity: another place scores nearly as well. Only the best is capped relative to the next one; the next ones relative to the best.
            var rival = i == 0 ? ordered.Skip(1).FirstOrDefault() : ordered[0];
            if (rival.Poi is not null && Math.Abs(item.Total - rival.Total) < AmbiguityMargin)
            {
                confidence = Math.Min(confidence, AmbiguousCap);
                signals.Add("ambiguous");
            }

            results.Add(new PlaceCandidateScore(item.Poi, Math.Round(confidence, 3), signals));
        }

        return results;
    }
}
