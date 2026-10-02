using System.Text.RegularExpressions;

namespace OnVoyage.Creators.Domain;

/// <summary>The <c>@handle</c> of a creator (F-26): 3 to 30 characters, unique without regard to case.</summary>
public static partial class Handles
{
    public const int MinLength = 3;
    public const int MaxLength = 30;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._]*$")]
    private static partial Regex Allowed();

    /// <summary>Removes the leading <c>@</c> and the blanks, then checks length and characters. The case is kept for display; uniqueness ignores it.</summary>
    public static bool TryNormalize(string? input, out string handle)
    {
        handle = (input ?? string.Empty).Trim().TrimStart('@');
        return handle.Length is >= MinLength and <= MaxLength && Allowed().IsMatch(handle) && !handle.EndsWith('.') && !handle.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>The first free variant of <paramref name="handle"/> (<c>marie</c>, <c>marie2</c>, <c>marie3</c>…), for the refusal message of a taken handle.</summary>
    public static string Suggest(string handle, Func<string, bool> isTaken)
    {
        for (var number = 2; number < 1_000; number++)
        {
            var suffix = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var candidate = (handle.Length + suffix.Length > MaxLength ? handle[..(MaxLength - suffix.Length)] : handle) + suffix;
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }

        return $"{handle[..Math.Min(handle.Length, MaxLength - 8)]}{Guid.NewGuid().ToString("N")[..8]}";
    }
}
