namespace OnVoyage.Web.Admin.Text;

public enum DiffKind
{
    Same,
    Added,
    Removed,
}

public sealed record DiffPart(DiffKind Kind, string Text);

/// <summary>Word-level difference between two versions of a story (the workshop shows what a regeneration or an edit changed).</summary>
public static class WordDiff
{
    /// <summary>Above this many words per side the quadratic table is skipped and the whole text is reported as replaced.</summary>
    public const int MaxWords = 3000;

    public static IReadOnlyList<DiffPart> Compare(string before, string after)
    {
        var a = Split(before);
        var b = Split(after);
        if (a.Length > MaxWords || b.Length > MaxWords)
        {
            return Merge([.. a.Select(word => new DiffPart(DiffKind.Removed, word)), .. b.Select(word => new DiffPart(DiffKind.Added, word))]);
        }

        // Longest common subsequence of words, then walk it.
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        List<DiffPart> parts = [];
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                parts.Add(new DiffPart(DiffKind.Same, a[x]));
                x++;
                y++;
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                parts.Add(new DiffPart(DiffKind.Removed, a[x++]));
            }
            else
            {
                parts.Add(new DiffPart(DiffKind.Added, b[y++]));
            }
        }

        while (x < a.Length)
        {
            parts.Add(new DiffPart(DiffKind.Removed, a[x++]));
        }

        while (y < b.Length)
        {
            parts.Add(new DiffPart(DiffKind.Added, b[y++]));
        }

        return Merge(parts);
    }

    private static string[] Split(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Consecutive words of the same kind become one part.</summary>
    private static List<DiffPart> Merge(IEnumerable<DiffPart> parts)
    {
        List<DiffPart> merged = [];
        foreach (var part in parts)
        {
            if (merged.Count > 0 && merged[^1].Kind == part.Kind)
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + " " + part.Text };
            }
            else
            {
                merged.Add(part);
            }
        }

        return merged;
    }
}
