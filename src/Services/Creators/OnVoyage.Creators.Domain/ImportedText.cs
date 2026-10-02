using System.Globalization;
using System.Text.RegularExpressions;

namespace OnVoyage.Creators.Domain;

/// <summary>
/// Reads the chapters a creator wrote in the description of a video (<c>02:15 Gordes</c>). YouTube itself only turns a list into chapters when it
/// starts at 0:00, but creators write all sorts of lists: this accepts a time at the start of a line (with an optional bullet, brackets or a
/// separator after it) or at its end (<c>Gordes - 02:15</c>), in <c>m:ss</c>, <c>mm:ss</c> or <c>h:mm:ss</c>.
/// </summary>
public static partial class ChapterParser
{
    // [bullet] [open] time [close] [separator] title
    [GeneratedRegex(@"^\s*(?:[-*•▶►➤→>]\s*)?[\[(]?\s*(?<time>(?:\d{1,2}:)?\d{1,2}:\d{2})\s*[\])]?\s*(?:[-–—:|.·»]\s*)?(?<title>\S.*?)\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex Leading();

    // title [separator] [open] time [close]
    [GeneratedRegex(@"^\s*(?<title>\S.*?)\s*[-–—:|·]?\s*[\[(]?\s*(?<time>(?:\d{1,2}:)?\d{1,2}:\d{2})\s*[\])]?\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex Trailing();

    /// <summary>The chapters of a description, in time order, without duplicated times; capped at <see cref="ContentRules.MaxChapters"/>. A title is cut at 100 characters.</summary>
    public static IReadOnlyList<Chapter> Parse(string? description, int? durationSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return [];
        }

        Dictionary<int, Chapter> chapters = [];
        foreach (var line in description.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length > 300)
            {
                continue;
            }

            var found = Leading().Match(line);
            if (!found.Success)
            {
                found = Trailing().Match(line);
            }

            if (!found.Success || !TryTime(found.Groups["time"].Value, out var seconds) || durationSeconds is { } duration && seconds > duration)
            {
                continue;
            }

            var title = found.Groups["title"].Value.Trim().TrimEnd('-', '–', '—', ':', '|').Trim();
            if (title.Length > 100)
            {
                title = title[..100].TrimEnd();
            }

            if (title.Length == 0)
            {
                continue;
            }

            chapters.TryAdd(seconds, new Chapter(seconds, title));
        }

        return [.. chapters.Values.OrderBy(chapter => chapter.StartSeconds).Take(ContentRules.MaxChapters)];
    }

    public static bool TryTime(string text, out int seconds)
    {
        seconds = 0;
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3 || parts.Any(part => part.Length is 0 or > 2 || !part.All(char.IsAsciiDigit)))
        {
            return false;
        }

        var numbers = parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        if (numbers[^1] > 59 || numbers.Length == 3 && numbers[1] > 59)
        {
            return false;
        }

        seconds = numbers.Aggregate(0, (total, value) => total * 60 + value);
        return true;
    }
}

/// <summary>An ISO 8601 duration as YouTube gives it (<c>PT1H2M3S</c>, <c>PT45S</c>, <c>P0D</c> for a live that has not started).</summary>
public static partial class IsoDuration
{
    [GeneratedRegex(@"^P(?:(?<d>\d+)D)?(?:T(?:(?<h>\d+)H)?(?:(?<m>\d+)M)?(?:(?<s>\d+)S)?)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex Pattern();

    public static int? ToSeconds(string? text)
    {
        if (string.IsNullOrEmpty(text) || Pattern().Match(text) is not { Success: true } match)
        {
            return null;
        }

        int Part(string name) => match.Groups[name].Success ? int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
        var total = (Part("d") * 86400L) + (Part("h") * 3600L) + (Part("m") * 60L) + Part("s");
        return total is > 0 and <= int.MaxValue ? (int)total : null;
    }
}

/// <summary>
/// The hashtags that declare a commercial collaboration (F-27, F-33): <c>#publicité</c>, <c>#ad</c>, <c>#sponsorisé</c>, with the usual variants.
/// Whole hashtags only (<c>#adventure</c> is not <c>#ad</c>), case and accents ignored. A detection makes the content carry the « Publicité » label.
/// </summary>
public static partial class CommercialDisclosure
{
    private static readonly HashSet<string> Tags = new(StringComparer.Ordinal)
    {
        "publicite", "pub", "ad", "ads", "sponsorise", "sponsorisee", "sponso", "sponsored", "sponsoredpost", "collaborationcommerciale", "partenariatremunere", "partenariatcommercial", "paidpartnership", "paidpartner",
    };

    [GeneratedRegex(@"#([\p{L}\p{N}_]+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex Hashtag();

    public static bool Detects(params string?[] texts) =>
        texts.Where(text => !string.IsNullOrEmpty(text)).Any(text => Hashtag().Matches(text!).Any(match => Tags.Contains(PoiEntry.Fold(match.Groups[1].Value).Replace("_", string.Empty, StringComparison.Ordinal))));
}
