using System.Globalization;
using OnVoyage.Creators.Contracts;

namespace OnVoyage.Web.Admin.Text;

/// <summary>The chapters of a video as the editor types them, one per line as in a YouTube description: <c>02:15 Gordes</c> or <c>1:02:15 Roussillon</c>.</summary>
public static class ChapterText
{
    public static (IReadOnlyList<ChapterDto>? Chapters, string? Error) Parse(string? text)
    {
        List<ChapterDto> chapters = [];
        foreach (var raw in (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = raw.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0 || !TryParseTime(raw[..space], out var seconds) || raw[(space + 1)..].Trim() is not { Length: > 0 } title)
            {
                return (null, $"Chapitre illisible : « {raw} ». Écrivez « 02:15 Gordes » (minutes:secondes puis le titre).");
            }

            chapters.Add(new ChapterDto(seconds, title));
        }

        return (chapters, null);
    }

    public static string Format(IEnumerable<ChapterDto> chapters) =>
        string.Join('\n', chapters.Select(chapter => $"{FormatTime(chapter.StartSeconds)} {chapter.Title}"));

    public static string FormatTime(int seconds) =>
        seconds >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds % 3600 / 60:00}:{seconds % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:00}:{seconds % 60:00}");

    private static bool TryParseTime(string text, out int seconds)
    {
        seconds = 0;
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3 || parts.Any(part => part.Length is 0 or > 2 || !part.All(char.IsAsciiDigit)))
        {
            return false;
        }

        var numbers = parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        if (numbers[^1] > 59 || (numbers.Length == 3 && numbers[1] > 59))
        {
            return false;
        }

        seconds = numbers.Aggregate(0, (total, value) => (total * 60) + value);
        return true;
    }
}
