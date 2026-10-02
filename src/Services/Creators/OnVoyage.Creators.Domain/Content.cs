using System.Globalization;
using System.Text.RegularExpressions;

namespace OnVoyage.Creators.Domain;

public static class ContentPlatforms
{
    public const string Instagram = "instagram";
    public const string YouTube = "youtube";
    public const string TikTok = "tiktok";
}

public static class ContentKinds
{
    public const string Video = "video";
    public const string Photo = "photo";
    public const string Carousel = "carousel";
    public const string Article = "article";

    public static IReadOnlyList<string> All { get; } = [Video, Photo, Carousel, Article];
}

public static class ContentStatuses
{
    /// <summary>Online: the reference is shown (when a validated link points to it).</summary>
    public const string Imported = "imported";
    public const string Hidden = "hidden";
    public const string Removed = "removed";
}

/// <summary>A chapter of a video (<c>02:15 Gordes</c>): one place per chapter, with the timestamped link out.</summary>
public sealed record Chapter(int StartSeconds, string Title);

/// <summary>
/// A content stays on its platform (D-17): only the reference is stored (identifier, link, title, date, duration), never the media itself.
/// </summary>
public sealed record ContentItem(
    Guid Id,
    Guid CreatorId,
    string Platform,
    string ExternalId,
    string Permalink,
    string Title,
    string? CaptionExcerpt,
    DateTimeOffset? PublishedAt,
    int? DurationSeconds,
    string Kind,
    string? CoverPath,
    IReadOnlyList<Chapter> Chapters,
    bool IsCommercial,
    string Status)
{
    public const int MaxCaptionExcerpt = 500;
    public const int MaxTitle = 200;

    public bool IsOnline => Status == ContentStatuses.Imported;

    /// <summary>The link out for a place: timestamped (<c>&amp;t=135s</c>) when the link points to a chapter of a YouTube video.</summary>
    public string UrlAt(int? startSeconds) => ContentUrls.At(Platform, Permalink, startSeconds);
}

public sealed record ParsedContentUrl(string Platform, string ExternalId, string Permalink, string DefaultKind);

/// <summary>Recognizes the public URL of a video, a reel or a post on the three platforms and gives its canonical form.</summary>
public static partial class ContentUrls
{
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex YouTubeId();

    [GeneratedRegex("^[A-Za-z0-9_-]{5,30}$")]
    private static partial Regex InstagramCode();

    [GeneratedRegex("^[0-9]{6,25}$")]
    private static partial Regex TikTokId();

    [GeneratedRegex("^@[A-Za-z0-9._]{1,40}$")]
    private static partial Regex TikTokUser();

    public static ParsedContentUrl? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 500 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal) || host.StartsWith("m.", StringComparison.Ordinal))
        {
            host = host[(host.IndexOf('.', StringComparison.Ordinal) + 1)..];
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return host switch
        {
            "youtube.com" => YouTube(uri, segments),
            "youtu.be" when segments.Length == 1 && YouTubeId().IsMatch(segments[0]) => YouTubeVideo(segments[0]),
            "instagram.com" when segments.Length >= 2 && segments[0] is "p" or "reel" or "reels" or "tv" && InstagramCode().IsMatch(segments[1]) =>
                new ParsedContentUrl(ContentPlatforms.Instagram, segments[1], $"https://www.instagram.com/{(segments[0] == "reels" ? "reel" : segments[0])}/{segments[1]}/", segments[0] == "p" ? ContentKinds.Photo : ContentKinds.Video),
            "tiktok.com" when segments.Length == 3 && TikTokUser().IsMatch(segments[0]) && segments[1] == "video" && TikTokId().IsMatch(segments[2]) =>
                new ParsedContentUrl(ContentPlatforms.TikTok, segments[2], $"https://www.tiktok.com/{segments[0]}/video/{segments[2]}", ContentKinds.Video),
            _ => null,
        };
    }

    private static ParsedContentUrl? YouTube(Uri uri, string[] segments)
    {
        if (segments is ["watch"] && ParseQuery(uri.Query, "v") is { } id && YouTubeId().IsMatch(id))
        {
            return YouTubeVideo(id);
        }

        return segments.Length == 2 && segments[0] is "shorts" or "embed" or "live" && YouTubeId().IsMatch(segments[1]) ? YouTubeVideo(segments[1]) : null;
    }

    private static ParsedContentUrl YouTubeVideo(string id) => new(ContentPlatforms.YouTube, id, $"https://www.youtube.com/watch?v={id}", ContentKinds.Video);

    private static string? ParseQuery(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts[0] == key && parts.Length == 2)
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return null;
    }

    public static string At(string platform, string permalink, int? startSeconds)
    {
        if (startSeconds is not { } seconds || seconds <= 0 || platform != ContentPlatforms.YouTube)
        {
            return permalink;
        }

        return $"{permalink}{(permalink.Contains('?', StringComparison.Ordinal) ? '&' : '?')}t={seconds.ToString(CultureInfo.InvariantCulture)}s";
    }
}

public static class ContentRules
{
    public const int MaxChapters = 100;

    /// <summary>Titles are trimmed, chapters are ordered by time; a title, a negative time or a duplicated time is refused.</summary>
    public static (IReadOnlyList<Chapter>? Chapters, Violation? Violation) NormalizeChapters(IEnumerable<Chapter>? chapters, int? durationSeconds)
    {
        var list = (chapters ?? []).Select(chapter => chapter with { Title = chapter.Title?.Trim() ?? string.Empty }).OrderBy(chapter => chapter.StartSeconds).ToList();
        if (list.Count > MaxChapters
            || list.Any(chapter => chapter.StartSeconds < 0 || chapter.Title.Length is 0 or > 100 || durationSeconds is { } duration && chapter.StartSeconds > duration)
            || list.Select(chapter => chapter.StartSeconds).Distinct().Count() != list.Count)
        {
            return (null, new Violation("validation", "Les chapitres ont chacun un titre (100 caractères) et un horodatage distinct dans la durée de la vidéo."));
        }

        return (list, null);
    }

    public static Violation? CheckText(string title, string? excerpt, string? coverPath, int? durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > ContentItem.MaxTitle)
        {
            return new Violation("validation", $"Le titre est obligatoire ({ContentItem.MaxTitle} caractères au plus).");
        }

        if (excerpt is { Length: > ContentItem.MaxCaptionExcerpt })
        {
            return new Violation("validation", $"L'extrait de légende fait {ContentItem.MaxCaptionExcerpt} caractères au plus.");
        }

        if (coverPath is { Length: > CreatorProfileRules.MaxPathLength } || coverPath is not null && coverPath.Any(char.IsWhiteSpace))
        {
            return new Violation("validation", "Le chemin de la vignette est invalide.");
        }

        return durationSeconds is < 0 ? new Violation("validation", "La durée ne peut pas être négative.") : null;
    }
}
